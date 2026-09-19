using System.Collections.Concurrent;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// Dispatches RPC calls (unary and streaming) from a service interface to a plain
/// implementation object. See docs/roadmap.md — auth lands in a later milestone;
/// `__transport_options__` (M14) is implemented here, and `__describe__` is retired (see
/// <see cref="RetiredDescribeMethodName"/>).
/// </summary>
public sealed class RpcServer
{
    private readonly IReadOnlyDictionary<string, RpcMethodInfo> _methods;
    private readonly object _implementation;
    private readonly string _serverId;
    private readonly IAccessLogSink? _accessLog;
    private readonly IRpcDispatchHook? _dispatchHook;
    private readonly string? _expectedProtocolVersion;
    private readonly IdentityImpl? _identity;
    private readonly IReadOnlyDictionary<string, RpcMethodInfo> _identityMethods;
    private readonly IReadOnlySet<string> _methodNames;
    private readonly IReadOnlySet<string> _identityMethodNames;

    /// <summary>Memoised <see cref="BindingHashFor"/> results, keyed by protocol name.</summary>
    /// <remarks>
    /// Canonicalising and digesting a description is not free and the answer never changes for
    /// the life of a server, while an access record wants it on every call. Only hosted names
    /// are cached: the key would otherwise be chosen by whoever sent the request.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _bindingHashes = new(StringComparer.Ordinal);

    /// <summary>The service interface's simple name — the access log's <c>protocol</c> field.</summary>
    public string ProtocolName { get; }

    /// <summary>
    /// The canonical SHA-256 digest of the application protocol's description — the access log's
    /// <c>protocol_hash</c> field for a call this server's own binding owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same digest reflection reports for this protocol, computed the same way
    /// (<see cref="Hash.ProtocolHash.ComputeProtocolHash"/>, WIRE_PROTOCOL.md §14), so it is
    /// byte-identical to every other port hosting the same protocol. It used to be a port-local
    /// digest over a StringBuilder of Arrow <c>TypeId</c>s, which was a stable, real value and
    /// also a useless one: <c>access-log-spec.md</c> §3 makes <c>protocol_hash</c> "the registry
    /// key when decoding archived records", and a registry is keyed by what
    /// <c>list_protocols</c>/<c>describe</c> report. A record carrying the other digest names a
    /// key that is in no registry, and nothing about it looks wrong -- it is 64 lowercase hex
    /// characters, it passes the schema, it groups plausibly on a dashboard.
    /// </para>
    /// <para>
    /// Computed once on first use rather than in the constructor: <see cref="Hash.TypeTokens"/>
    /// refuses to spell an Arrow type it has no canonical token for, and that refusal belongs on
    /// the call that asked rather than on every server's startup path. A server that cannot
    /// produce this digest also cannot answer <c>list_protocols</c>, which every conformant
    /// server hosts, so it is already broken rather than newly broken here.
    /// </para>
    /// </remarks>
    public string ProtocolHash => BindingHashFor(ProtocolName);

    public string? ServerVersion { get; init; }

    /// <summary>
    /// The registered methods, keyed by wire name — exposed for transports (see
    /// <c>QueryFarm.VgiRpc.Http</c>) that dispatch outside <see cref="ServeAsync"/>'s own loop
    /// and need to resolve a method themselves. Mirrors Python's public <c>RpcServer.methods</c>.
    /// </summary>
    public IReadOnlyDictionary<string, RpcMethodInfo> Methods => _methods;

    /// <summary>The service implementation instance, for transports that invoke methods directly
    /// rather than through <see cref="ServeOneAsync"/>.</summary>
    internal object Implementation => _implementation;

    /// <summary>This server instance's id — see <see cref="AccessLogRecord.ServerId"/>.</summary>
    internal string ServerId => _serverId;

    /// <summary>The configured access-log sink, or <see langword="null"/> if none — for
    /// transports that emit their own <see cref="AccessLogRecord"/>s outside this dispatch loop.</summary>
    internal IAccessLogSink? AccessLog => _accessLog;

    /// <summary>The configured dispatch hook (see <see cref="IRpcDispatchHook"/>, M16), or
    /// <see langword="null"/> if none — for transports (<c>QueryFarm.VgiRpc.Http</c>) that
    /// dispatch outside this class's own <see cref="ServeOneAsync"/>/<see cref="ServeStreamAsync"/>
    /// loop and so call <see cref="IRpcDispatchHook.OnDispatchStart"/>/
    /// <see cref="IRpcDispatchHook.OnDispatchEnd"/> themselves, around their own dispatch points.</summary>
    internal IRpcDispatchHook? DispatchHook => _dispatchHook;

    /// <param name="serviceInterface">The service interface type to reflect method schemas from.</param>
    /// <param name="implementation">The service implementation instance to dispatch calls to.</param>
    /// <param name="serverId">This server instance's id — a random GUID when omitted.</param>
    /// <param name="accessLog">Optional access-log sink — see <see cref="AccessLog"/>.</param>
    /// <param name="dispatchHooks">Zero or more observability hooks (M16) — e.g.
    /// <c>QueryFarm.VgiRpc.OpenTelemetry.OtelDispatchHook</c>,
    /// <c>QueryFarm.VgiRpc.Sentry.SentryDispatchHook</c> — fanned out via
    /// <see cref="CompositeDispatchHook"/>. Empty/<see langword="null"/> (the default) means no
    /// hooks at all, matching every other optional-observability feature's opt-in-by-default
    /// posture in this port.</param>
    /// <param name="expectedProtocolVersion">When non-<see langword="null"/>, every request's
    /// <c>vgi_rpc.protocol_version</c> custom_metadata (canonical semver <c>MAJOR.MINOR.PATCH</c>)
    /// is required to share this value's major AND minor components — an application-level
    /// protocol contract layered on top of this transport, matching the canonical Python
    /// implementation's <c>_check_protocol_version</c> (patch is deliberately ignored). A missing
    /// or malformed client value, or a major/minor mismatch, is refused with a
    /// <see cref="ProtocolVersionException"/> before dispatch. <see langword="null"/> (the
    /// default) disables the check entirely — this transport layer is protocol-agnostic and most
    /// callers (including this repo's own test suite, whose <c>RpcConnection</c> client never
    /// sends this key) have no such application-level version to enforce.</param>
    /// <param name="identity">An <see cref="IdentityImpl"/> to host <c>vgi_rpc.Identity.v1</c>
    /// alongside the application protocol. <see langword="null"/> (the default) means the
    /// protocol is not hosted at all — absent rather than routed-and-refusing, which is what
    /// keeps a dependency upgrade from growing a credential-to-identity oracle on every existing
    /// worker. An instance with no hooks configured is treated the same way.</param>
    public RpcServer(
        Type serviceInterface, object implementation, string? serverId = null, IAccessLogSink? accessLog = null,
        IReadOnlyList<IRpcDispatchHook>? dispatchHooks = null, string? expectedProtocolVersion = null,
        IdentityImpl? identity = null)
    {
        _methods = ServiceRegistry.GetMethods(serviceInterface);
        _implementation = implementation;
        _serverId = serverId ?? Guid.NewGuid().ToString("n");
        _accessLog = accessLog;
        _expectedProtocolVersion = expectedProtocolVersion;
        _dispatchHook = dispatchHooks is { Count: > 0 } ? new CompositeDispatchHook(dispatchHooks) : null;
        // A [ProtocolName] declaration when the contract carries one, else the type name with
        // C#'s interface `I` prefix stripped: the protocol name is the wire identity, it is in
        // the protocol hash, and carrying a language naming convention onto the wire makes this
        // port speak a differently-named protocol from the one it is meant to implement. A name
        // no C# identifier can spell -- `vgi.v2` -- is why declaring it has to be possible at all.
        // The same resolution the clients use to address this server -- shared rather than
        // duplicated, so the two cannot drift into hosting and addressing different names.
        // Resolved here, in the constructor: an unroutable declaration fails when the server is
        // built rather than on every request.
        ProtocolName = WireNaming.ForProtocol(serviceInterface);

        // Identity is registered AFTER reflection (which this port hosts unconditionally, and
        // which is listed ahead of it in HostedProtocols below) so that it appears in
        // reflection's own output -- a client discovers that this worker resolves credentials
        // the same way it discovers everything else, rather than by calling and reading an
        // error. And only when the deployment configured it: absent by default, and absent
        // rather than routed-and-refusing when omitted, which is what keeps a dependency
        // upgrade from growing a credential-to-identity oracle on every existing worker.
        //
        // The method set narrows to the hooks that exist (see IdentityProtocol.MethodsFor), so
        // the hash narrows with it: a worker offering half the methods is not offering the same
        // surface and must not claim the same fingerprint.
        var offered = identity?.OfferedMethods();
        _identity = offered is { Count: > 0 } ? identity : null;
        _identityMethods = offered is { Count: > 0 }
            ? IdentityProtocol.MethodsFor(offered)
            : new Dictionary<string, RpcMethodInfo>(StringComparer.Ordinal);
        _methodNames = new HashSet<string>(_methods.Keys, StringComparer.Ordinal);
        _identityMethodNames = new HashSet<string>(_identityMethods.Keys, StringComparer.Ordinal);
    }

    /// <summary>The protocols this server hosts, in registration order.</summary>
    /// <remarks>
    /// The application protocol first (it is what <see cref="ProtocolName"/> reports and what
    /// framework endpoints with no owning protocol log against), then <c>vgi_rpc.Reflection.v1</c>,
    /// then <c>vgi_rpc.Identity.v1</c> when a deployment configured it. Mirrors the canonical
    /// Python implementation's <c>RpcServer.bindings</c> ordering, and is the same order
    /// reflection reports them in.
    /// </remarks>
    public IReadOnlyList<string> HostedProtocols =>
        _identity is null
            ? [ProtocolName, ReflectionProtocol.ProtocolName]
            : [ProtocolName, ReflectionProtocol.ProtocolName, IdentityProtocol.ProtocolName];

    /// <summary>The method names <paramref name="protocolName"/> answers, or <see langword="null"/>
    /// if this server does not host that protocol.</summary>
    /// <remarks>
    /// The names side of <see cref="MethodsForProtocol"/>, and for every hosted protocol —
    /// reflection included — it is that table's keys: what a protocol dispatches and what it
    /// describes are the same set. This is the accessor a transport resolves a path against, and
    /// the one that lets "protocol not hosted" (404) and "hosted, no such method" (404, different
    /// <c>error_kind</c>) stay distinguishable, which is the documented capability-probe signal.
    /// </remarks>
    public IReadOnlySet<string>? MethodNamesForProtocol(string protocolName)
    {
        if (protocolName == ProtocolName) return _methodNames;
        if (protocolName == ReflectionProtocol.ProtocolName) return ReflectionProtocol.MethodNames;
        if (protocolName == IdentityProtocol.ProtocolName && _identity is not null) return _identityMethodNames;
        return null;
    }

    /// <summary>Whether <paramref name="protocolName"/> is served by the framework itself
    /// (reflection, identity) rather than by the application implementation.</summary>
    /// <remarks>
    /// A framework protocol is served by the framework on every transport, so nothing about
    /// hosting it can perturb what the application protocol puts on the wire. Transports that
    /// dispatch outside <see cref="ServeOneAsync"/> — HTTP — branch on this to route into
    /// <see cref="ServeFrameworkUnaryAsync"/> instead of invoking
    /// <see cref="Implementation"/>.
    /// </remarks>
    internal static bool IsFrameworkProtocol(string protocolName) =>
        protocolName == ReflectionProtocol.ProtocolName || protocolName == IdentityProtocol.ProtocolName;

    /// <summary>The outcome of one framework-protocol dispatch: the schema the response stream
    /// was written with, plus what to record about it. Handed back rather than logged here
    /// because a transport that dispatches on its own (HTTP) owns its own access record —
    /// including the HTTP status the framework knows nothing about.</summary>
    internal readonly record struct FrameworkDispatch(Schema Schema, string Status, string ErrorType, string ErrorMessage)
    {
        public static FrameworkDispatch Ok(Schema schema) => new(schema, "ok", "", "");

        public static FrameworkDispatch Failed(Schema schema, string errorType, string errorMessage) =>
            new(schema, "error", errorType, errorMessage);
    }

    /// <summary>Serves one unary call to a framework-owned protocol, writing the complete IPC
    /// response (result or in-band error batch) into <paramref name="output"/>.</summary>
    /// <remarks>
    /// The transport-neutral half of <see cref="ServeOneAsync"/>'s reflection/identity branches,
    /// extracted so HTTP — which has one request body in and one response body out, and so cannot
    /// drive a serve loop — reaches the same code rather than a second copy of it. The caller
    /// supplies the <see cref="ICallContext"/>, which is how the caller's authenticated identity
    /// reaches identity's guards: on HTTP that identity comes from the request, not from a
    /// connection-scoped peer.
    /// </remarks>
    internal Task<FrameworkDispatch> ServeFrameworkUnaryAsync(
        string protocolName, string methodName, AnnotatedBatch request, ICallContext callContext,
        Stream output, CancellationToken cancellationToken)
    {
        if (protocolName == ReflectionProtocol.ProtocolName)
        {
            return ServeReflectionAsync(output, methodName, request, cancellationToken);
        }

        if (protocolName == IdentityProtocol.ProtocolName && _identity is not null)
        {
            return ServeIdentityAsync(output, methodName, request, callContext, emitAccessLog: false, cancellationToken);
        }

        throw new ArgumentException($"'{protocolName}' is not a framework protocol hosted here.", nameof(protocolName));
    }

    /// <summary>The canonical hash of one hosted protocol — the access log's
    /// <c>protocol_hash</c> for any call that protocol's binding owns.</summary>
    /// <remarks>
    /// The per-binding counterpart of <see cref="ProtocolHash"/> (which is this, for the
    /// application protocol), and the accessor a transport dispatching outside the serve loop
    /// reaches for so its records carry the digest of the protocol they name rather than the
    /// server's primary. Mirrors the canonical Python implementation's
    /// <c>RpcServer.protocol_hash_for</c>.
    /// </remarks>
    public string ProtocolHashFor(string protocolName) => BindingHashFor(protocolName);

    /// <summary>The methods hosted under <paramref name="protocolName"/>, or <see langword="null"/>
    /// if this server does not host that protocol.</summary>
    /// <remarks>
    /// Reflection answers with its own two methods, like any other binding: self-description is
    /// not special-cased, so <c>describe("vgi_rpc.Reflection.v1")</c> reports them and its
    /// protocol hash is taken over them (see <see cref="Reflection.IReflectionProtocol"/>).
    /// Identity's table is the narrowed one, so asking this is how a caller sees that a
    /// deployment hosts <c>introspect_token</c> and not <c>issue_grant</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, RpcMethodInfo>? MethodsForProtocol(string protocolName)
    {
        if (protocolName == ProtocolName) return _methods;
        if (protocolName == ReflectionProtocol.ProtocolName) return ReflectionProtocol.Methods;
        if (protocolName == IdentityProtocol.ProtocolName && _identity is not null) return _identityMethods;
        return null;
    }

    /// <summary>Serves requests off <paramref name="transport"/> until the channel closes.</summary>
    public async Task ServeAsync(IRpcTransport transport, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var more = await ServeOneAsync(transport, cancellationToken).ConfigureAwait(false);
            if (!more)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handles exactly one request/response cycle. Returns <see langword="false"/> when the
    /// channel has reached a clean end-of-stream with no request to read (the normal way a
    /// <see cref="ServeAsync"/> loop terminates); protocol/dispatch errors are written back to
    /// the client as an error response and this returns <see langword="true"/> so the caller's
    /// serve loop continues.
    /// </summary>
    public async Task<bool> ServeOneAsync(IRpcTransport transport, CancellationToken cancellationToken = default)
    {
        AnnotatedBatch? request;
        try
        {
            // Deliberately NOT `using var` held for the rest of this method: a stream method
            // opens a second WireReader over the same transport.Input for its tick/exchange
            // loop, and some Stream implementations (observed with NetworkStream — Unix/TCP
            // sockets) read ahead into an internal buffer, silently stealing bytes that belong
            // to that second reader if the first one is still alive when it's constructed.
            // Disposing this one immediately after the request is fully read avoids that.
            using var reader = new WireReader(transport.Input);
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            request = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (request is not null)
            {
                // The request is a self-contained IPC stream (schema + 1 batch + EOS) the client
                // fully wrote before this call returns control, but ReadNextAsync only reads the
                // one batch — it never consumes the trailing EOS marker. Leaving that unread
                // desyncs the NEXT reader constructed over this same channel (this method's own
                // ServeStreamAsync call below opens a fresh one for the tick/exchange loop).
                // Mirrors vgi-rpc-go's drainInputStream / vgi-rpc-java's IpcStreamReader.drain(),
                // both called at this exact point (right after a successful read, before any
                // validation that might short-circuit).
                await reader.DrainRemainingBatchesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PayloadTooLargeException exc) when (!cancellationToken.IsCancellationRequested)
        {
            // Unlike the catch-all below, WireReader has already drained the oversized body off
            // the wire before throwing this — the connection is still in sync, so refuse with a
            // normal typed error and keep serving instead of tearing the whole connection down.
            // See PayloadTooLargeException's doc comment and docs/roadmap.md M17.
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, exc, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The channel closed (cleanly or otherwise) before a full request arrived — the
            // normal way a ServeAsync loop ends when the client disconnects. Apache.Arrow
            // doesn't document a single exception type for "stream ended mid-schema", so this
            // catches broadly rather than risk an unhandled exception tearing down the worker
            // on a plain client disconnect.
            return false;
        }

        if (request is null)
        {
            return false;
        }

        using var requestOwner = new RecordBatchOwner(request.Batch);

        var methodName = request.GetMetadata(MetadataKeys.Method);
        if (methodName is null)
        {
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, new RpcException("RpcException", "Request batch is missing vgi_rpc.method metadata."), cancellationToken).ConfigureAwait(false);
            return true;
        }

        var requestVersion = request.GetMetadata(MetadataKeys.RequestVersion);
        if (requestVersion != MetadataKeys.CurrentRequestVersion)
        {
            await WriteErrorStreamAsync(
                transport.Output,
                s_emptySchema,
                new VersionException(nameof(VersionException), $"Unsupported request_version '{requestVersion}' (expected '{MetadataKeys.CurrentRequestVersion}')."),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Reflection is a co-hosted protocol, routed by the same key as
        // everything else and appearing in its own output. Handled before the
        // version gate because it is exempt from it: this is what a
        // version-mismatched client calls to learn what mismatched, and gating
        // it would deny the client the diagnosis it came for.
        if (request.GetMetadata(MetadataKeys.Protocol) == ReflectionProtocol.ProtocolName)
        {
            var reflectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var reflectionOutcome = await ServeReflectionAsync(transport.Output, methodName, request, cancellationToken).ConfigureAwait(false);
            // Logged here rather than inside ServeReflectionAsync because HTTP dispatches the
            // same method itself and owns its own record (including the HTTP status the
            // framework knows nothing about) -- the same split identity already uses. Logging it
            // at all is the point: HTTP already produced a reflection record while this
            // transport produced none, and two transports disagreeing about whether a call
            // happened is how an audit trail comes to have a hole that looks like quiet traffic.
            await EmitAccessLogAsync(
                methodName, "unary", reflectionOutcome.Status, reflectionOutcome.ErrorType,
                reflectionOutcome.ErrorMessage, reflectionStart,
                requestForLog: request,
                protocol: ReflectionProtocol.ProtocolName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Identity, like reflection, is a co-hosted framework protocol routed by the same key.
        // Handled before the version gate for the same structural reason it is in the canonical
        // Python implementation: the gate compares against the *application* protocol's declared
        // version, and `vgi_rpc.Identity.v1` declares none of its own. Gating a framework
        // protocol on an unrelated contract would make a proxy's ability to resolve a credential
        // depend on whether it had been upgraded in lockstep with the application.
        if (_identity is not null && request.GetMetadata(MetadataKeys.Protocol) == IdentityProtocol.ProtocolName)
        {
            _ = await ServeIdentityAsync(
                transport.Output, methodName, request, new BufferedCallContext(),
                emitAccessLog: true, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // `__describe__` is *retired*, not merely absent, and the two are indistinguishable from
        // the caller's side while needing opposite fixes -- update the client, or reconfigure the
        // server. Answered here, ahead of both the routing check and the version gate, for the
        // same reason reflection is: this is what a stale or mismatched client calls to find out
        // what is wrong, so making it depend on naming a protocol it does not know about, or on
        // already agreeing about versions, would withhold the diagnosis exactly when it is
        // needed. Only this one reserved name is special-cased; every other keeps the plain
        // "no such method" answer below, which is what a client probing for an optional method
        // needs.
        if (methodName == RetiredDescribeMethodName)
        {
            await WriteErrorStreamAsync(
                transport.Output, s_emptySchema,
                new MethodNotImplementedException(RetiredDescribeMessage),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Routing, before the version gate and before dispatch. On a byte-stream transport
        // `vgi_rpc.protocol` is the ONLY carrier there is -- no path segment, no header -- so an
        // absent key is genuinely unroutable and dispatching anyway means landing on whichever
        // protocol happens to be registered first. That is the exact confused-deputy shape the
        // rule exists to prevent, and an intermediary that rebuilds a request and drops the field
        // has to be told rather than silently accommodated.
        //
        // (HTTP is the deliberate asymmetry: there the path segment has already resolved the
        // binding, so its dispatcher accepts an absent key and refuses only a disagreement. See
        // RpcHttpEndpoints.CheckProtocolAgreementAsync, which names what that gives up.)
        //
        // Reserved framework built-ins are server-level, owned by no protocol, and resolved
        // without routing -- `__transport_options__` below, and the retired `__describe__`
        // above.
        // Requiring one of them to name a protocol would break the diagnostic path a mismatched
        // client uses to find out *what* mismatched.
        if (!IsReservedMethodName(methodName)
            && RoutingFailure(request) is { } routingFailure)
        {
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, routingFailure, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_expectedProtocolVersion is not null && CheckProtocolVersion(request, _expectedProtocolVersion) is { } protocolMismatch)
        {
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, protocolMismatch, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // __transport_options__ (M14, WIRE_PROTOCOL.md §15): a built-in synthetic method,
        // parallel to __describe__, through which client and server negotiate transport
        // capabilities — chiefly SHM. Handled here rather than via _methods (it's not a real
        // service method) and answered unconditionally: this port always supports the SHM side
        // channel on every non-HTTP transport ServeAsync itself is ever invoked from (HTTP has
        // its own, entirely separate dispatch path in QueryFarm.VgiRpc.Http that never calls
        // this method at all — the same "exemption needs zero extra code" structural argument
        // M11's proxy-proof note made for its own HTTP-only exemptions).
        if (methodName == TransportOptionsMethodName)
        {
            var responseMetadata = new Dictionary<string, string>
            {
                [MetadataKeys.TransportShm] = "true",
                [MetadataKeys.ServerId] = _serverId,
                [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
            };
            await using var transportWriter = new WireWriter(transport.Output, s_emptySchema);
            await transportWriter.WriteOwnedBatchAsync(ValueCodec.EmptyRow(s_emptySchema), responseMetadata, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!_methods.TryGetValue(methodName, out var info))
        {
            var available = string.Join(", ", _methods.Keys.OrderBy(k => k, StringComparer.Ordinal));
            await WriteErrorStreamAsync(
                transport.Output,
                s_emptySchema,
                new MethodNotImplementedException($"Unknown method: '{methodName}'. Available methods: [{available}]"),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Dynamic SHM attach (M14): a client that has negotiated SHM support advertises its
        // segment in the request batch's own metadata (WIRE_PROTOCOL.md §11) — on every unary/
        // stream-init request, but NOT on later stream turns (see ServeStreamAsync's own SHM
        // handling for why that matters: the segment attached here must be threaded through and
        // reused for a stream's whole lifetime, not re-derived per turn). Malformed metadata or a
        // segment that fails to attach (wrong size, doesn't exist) is treated as "no SHM for this
        // request" rather than a hard error — matches Python's _maybe_attach_shm posture exactly:
        // a dynamically-attached segment is the caller's optimization, not a contract the server
        // must enforce.
        var shm = TryAttachShm(request);
        if (shm is not null)
        {
            try
            {
                var (resolvedBatch, resolvedMetadata, release) = await ShmPointerBatch.ResolveAsync(request.Batch, request.Metadata, shm, cancellationToken).ConfigureAwait(false);
                requestOwner.Replace(resolvedBatch);
                request = request with { Batch = resolvedBatch, Metadata = resolvedMetadata };
                release?.Invoke();
            }
            catch (Exception exc)
            {
                shm.Dispose();
                await WriteErrorStreamAsync(transport.Output, s_emptySchema, exc, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        object?[] args;
        try
        {
            args = ValueCodec.ExtractRow(request.Batch, info.ParameterTypes);
        }
        catch (Exception exc)
        {
            shm?.Dispose();
            await WriteErrorStreamAsync(transport.Output, info.ResultSchema, exc, cancellationToken).ConfigureAwait(false);
            await EmitAccessLogAsync(info.WireName, "unary", "error", exc.GetType().Name, exc.Message, System.Diagnostics.Stopwatch.GetTimestamp(), requestForLog: request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        if (info.Kind == RpcMethodKind.Stream)
        {
            // ServeStreamAsync owns shm from here on (a stream's whole lifetime, across every
            // turn) — it disposes it, ServeOneAsync must not.
            return await ServeStreamAsync(transport, info, args, start, shm, cancellationToken).ConfigureAwait(false);
        }

        using var ownedLargeBytesArguments = new LargeBytesBufferArgumentsOwner(args);
        using var shmForUnary = shm;
        await using var writer = new WireWriter(transport.Output, info.ResultSchema);
        var context = info.HasContextParameter ? new BufferedCallContext() : null;
        var status = "ok";
        var errorType = "";
        var errorMessage = "";
        Exception? hookError = null;
        var hookInfo = new DispatchHookInfo(info.WireName, "unary", ProtocolName, _serverId);
        var hookToken = _dispatchHook?.OnDispatchStart(hookInfo);
        try
        {
            var result = await info.InvokeAsync(_implementation, args, context).ConfigureAwait(false);
            using var ownedLargeBytesResult = result as LargeBytesBuffer;
            if (context is not null)
            {
                foreach (var logMessage in context.Buffered)
                {
                    await writer.WriteOwnedBatchAsync(ValueCodec.EmptyRow(info.ResultSchema), logMessage.AddToMetadata(), cancellationToken).ConfigureAwait(false);
                }
            }

            var resultBatch = info.ResultSchema.FieldsList.Count == 0
                ? ValueCodec.EmptyRow(info.ResultSchema)
                : ValueCodec.BuildRow(info.ResultSchema, [result]);
            IReadOnlyDictionary<string, string>? resultMetadata = null;
            using var resultOwner = new RecordBatchOwner(resultBatch);
            if (shmForUnary is not null)
            {
                (resultBatch, resultMetadata) = await ShmPointerBatch.MaybeWriteAsync(resultBatch, null, shmForUnary, cancellationToken).ConfigureAwait(false);
            }
            resultOwner.Replace(resultBatch);

            await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, resultMetadata), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            status = "error";
            errorType = actual.GetType().Name;
            errorMessage = actual.Message;
            hookError = actual;
            var metadata = LogMessage.FromException(actual).AddToMetadata();
            await writer.WriteOwnedBatchAsync(ValueCodec.EmptyRow(info.ResultSchema), metadata, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, hookError);
            await EmitAccessLogAsync(info.WireName, "unary", status, errorType, errorMessage, start, requestForLog: request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Drives a streaming call's lockstep turns: one continuous output IPC stream (opened once,
    /// for <see cref="IRpcStream.OutputSchema"/>) and one continuous input IPC stream (opened
    /// once, reading successive tick/exchange batches) for the lifetime of the call. See
    /// <see cref="StreamState"/>/<see cref="ProducerState"/>/<see cref="ExchangeState"/> and
    /// WIRE_PROTOCOL.md's lockstep streaming section (canonical Python repo).
    /// </summary>
    /// <param name="transport">The transport this stream's turns are read from/written to.</param>
    /// <param name="info">Reflection info for the RPC method that constructs this stream.</param>
    /// <param name="args">Already-extracted/resolved constructor arguments.</param>
    /// <param name="start">Timestamp (from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>)
    /// the whole call began, for access-log duration.</param>
    /// <param name="shm">The SHM segment attached from the stream-init request's own metadata
    /// (if any), owned by this method for the stream's whole lifetime and disposed on every exit
    /// path. Unlike unary calls, a stream's later turns never re-advertise the segment identity
    /// (WIRE_PROTOCOL.md §11 documents the pointer keys `SHM_OFFSET_KEY`/`SHM_LENGTH_KEY` a turn
    /// may carry, but the *segment identity* — `SHM_SEGMENT_NAME_KEY`/`SHM_SEGMENT_SIZE_KEY` — is
    /// only ever sent once, on the request that establishes the call; confirmed against the
    /// canonical Python client's own `_write_request`/`_write_batch` split), so this one
    /// attachment must be reused across every subsequent turn, never re-derived per turn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<bool> ServeStreamAsync(IRpcTransport transport, RpcMethodInfo info, object?[] args, long start, ShmSegment? shm, CancellationToken cancellationToken)
    {
        using var ownedShm = shm;

        // Required by access_log.schema.json whenever method_type=stream (no exception for the
        // error paths below) — generated up front so every exit from this method can log it.
        // Matches Python's uuid.uuid4().hex (32 lowercase hex chars).
        var streamId = Guid.NewGuid().ToString("N");

        var hookInfo = new DispatchHookInfo(info.WireName, "stream", ProtocolName, _serverId);
        var hookToken = _dispatchHook?.OnDispatchStart(hookInfo);

        var invokeContext = info.HasContextParameter ? new BufferedCallContext() : null;
        IRpcStream stream;
        using var ownedLargeBytesArguments = new LargeBytesBufferArgumentsOwner(args);
        try
        {
            var raw = await info.InvokeAsync(_implementation, args, invokeContext).ConfigureAwait(false);
            stream = (IRpcStream)raw!;
            ownedLargeBytesArguments.Dispose();
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, actual);
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, actual, cancellationToken).ConfigureAwait(false);
            // A stream request is followed by a second IPC stream -- the client's tick/exchange
            // input -- on the same channel, and a constructor that throws does not make the
            // client take it back. Left unread, the next ServeOneAsync parses that input stream
            // as a request ("missing vgi_rpc.method metadata") and answers the caller's *next*
            // call with that error, so one refused open poisons the connection. Consume it
            // through EOS, as the canonical Python server does (RpcServer._serve_stream). The
            // error is written and flushed first: a lockstep client sends its input EOS only
            // after it has read the error, so draining before replying would deadlock.
            await DrainAbandonedInputStreamAsync(transport, cancellationToken).ConfigureAwait(false);
            await EmitAccessLogAsync(info.WireName, "stream", "error", actual.GetType().Name, actual.Message, start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A stream header is its own complete IPC stream (schema + one row + EOS), written
        // before the main output stream begins — see IRpcStream.Header's doc comment.
        if (stream.Header is not null)
        {
            var headerType = stream.Header.GetType();
            var headerSchema = SchemaDerivation.InnerSchemaFor(headerType);
            var headerValues = headerSchema.FieldsList
                .Select(f => headerType.GetProperty(ValueCodec.FindClrPropertyName(headerType, f))!.GetValue(stream.Header))
                .ToList();
            var headerBatch = ValueCodec.BuildRow(headerSchema, headerValues);
            using var headerBatchOwner = new RecordBatchOwner(headerBatch);
            await using var headerWriter = new WireWriter(transport.Output, headerSchema);
            if (invokeContext is not null)
            {
                foreach (var logMessage in invokeContext.Buffered)
                {
                    await headerWriter.WriteOwnedBatchAsync(ValueCodec.EmptyRow(headerSchema), logMessage.AddToMetadata(), cancellationToken).ConfigureAwait(false);
                }

                invokeContext.Buffered.Clear();
            }

            await headerWriter.WriteBatchAsync(new AnnotatedBatch(headerBatch, null), cancellationToken).ConfigureAwait(false);
        }

        var outputSchema = stream.OutputSchema;
        await using var outputWriter = new WireWriter(transport.Output, outputSchema);
        // Write the schema eagerly, not lazily-on-first-batch: a stream that finishes with zero
        // batches (e.g. an empty producer) must still produce a valid (schema, EOS) IPC stream.
        await outputWriter.WriteStartAsync(cancellationToken).ConfigureAwait(false);

        if (invokeContext is not null)
        {
            foreach (var logMessage in invokeContext.Buffered)
            {
                await outputWriter.WriteOwnedBatchAsync(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata(), cancellationToken).ConfigureAwait(false);
            }
        }

        using var inputReader = new WireReader(transport.Input);
        try
        {
            _ = await inputReader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // client never opened the tick/exchange input stream
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, null);
            await EmitAccessLogAsync(info.WireName, "stream", "ok", "", "", start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        var streamStatus = "ok";
        var streamErrorType = "";
        var streamErrorMessage = "";
        Exception? streamHookError = null;
        // Set false only on the natural-EOS exit below, where ReadNextAsync already consumed the
        // input stream's own EOS marker — there is nothing left to drain, and on a shared,
        // persistent channel (pipe/stdio, where one worker process serves every call in the
        // session) calling ReadNextAsync again would read into whatever the client sends NEXT
        // (the following top-level request), corrupting its framing. Every other exit (cancel,
        // error, producer finish, mid-stream disconnect) can still have unread bytes queued.
        var inputNeedsDrain = true;
        while (true)
        {
            AnnotatedBatch? inputBatch;
            try
            {
                inputBatch = await inputReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                break; // client disconnected mid-stream
            }

            if (inputBatch is null)
            {
                inputNeedsDrain = false;
                break; // client closed its input stream (EOS) — the normal way an exchange ends
            }

            using var inputBatchOwner = new RecordBatchOwner(inputBatch.Batch);

            if (inputBatch.GetMetadata(MetadataKeys.Cancel) is not null)
            {
                stream.State.OnCancel(invokeContext);
                break;
            }

            using var collector = new OutputCollector(outputSchema);
            // Always construct a per-turn context — unlike invokeContext above (which mirrors
            // whether the RPC method that RETURNED the stream declared a ctx parameter, since
            // that gates a reflection-invoke arg count), StreamState.ProcessAsync's own signature
            // always accepts an ICallContext?, independent of the constructor method's shape. A
            // StreamState reading ctx.Session (sticky sessions, docs/roadmap.md M10) needs a real
            // object here even when the constructor method itself took no ctx param.
            var turnContext = new StreamCallContext(collector);
            try
            {
                // Per-turn SHM pointer resolution (M14) — reuses the ONE segment attached at
                // stream-init (ownedShm), never re-derived: see this method's doc comment on
                // `shm` for why later turns don't re-advertise the segment identity.
                if (ownedShm is not null)
                {
                    var (resolvedBatch, resolvedMetadata, release) = await ShmPointerBatch.ResolveAsync(inputBatch.Batch, inputBatch.Metadata, ownedShm, cancellationToken).ConfigureAwait(false);
                    inputBatch = inputBatch with { Batch = resolvedBatch, Metadata = resolvedMetadata };
                    inputBatchOwner.Replace(resolvedBatch);
                    release?.Invoke();
                }

                if (stream.InputSchema is { FieldsList.Count: > 0 } declaredInputSchema)
                {
                    var coercedBatch = ValueCodec.CoerceBatch(inputBatch.Batch, declaredInputSchema);
                    inputBatchOwner.ReplaceShared(coercedBatch);
                    inputBatch = inputBatch with { Batch = coercedBatch };
                }

                await stream.State.ProcessAsync(inputBatch, collector, turnContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                var actual = Unwrap(exc);
                streamStatus = "error";
                streamErrorType = actual.GetType().Name;
                streamErrorMessage = actual.Message;
                streamHookError = actual;
                var metadata = LogMessage.FromException(actual).AddToMetadata();
                await outputWriter.WriteOwnedBatchAsync(ValueCodec.EmptyRow(outputSchema), metadata, cancellationToken).ConfigureAwait(false);
                break;
            }

            foreach (var logMessage in collector.Logs)
            {
                await outputWriter.WriteOwnedBatchAsync(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata(), cancellationToken).ConfigureAwait(false);
            }

            var emitted = collector.DetachEmittedBatch();
            if (emitted is not null)
            {
                using var emittedOwner = new RecordBatchOwner(emitted);
                var emittedMetadata = collector.EmittedMetadata;
                if (ownedShm is not null)
                {
                    (emitted, emittedMetadata) = await ShmPointerBatch.MaybeWriteAsync(emitted, emittedMetadata, ownedShm, cancellationToken).ConfigureAwait(false);
                    emittedOwner.Replace(emitted);
                }

                await outputWriter.WriteBatchAsync(new AnnotatedBatch(emitted, emittedMetadata), cancellationToken).ConfigureAwait(false);
            }

            // A streaming turn has no EOS marker, so buffered transports need an explicit
            // visibility boundary before the server waits for the client's next input turn.
            await outputWriter.FlushAsync(cancellationToken).ConfigureAwait(false);


            if (collector.Finished)
            {
                break;
            }
        }

        // Close the output stream (sends EOS to the client) FIRST, then drain whatever the
        // client's input stream still has queued — draining before closing output can deadlock:
        // a client blocked reading the response it's waiting for hasn't closed its own writer
        // yet, so the drain read would block forever. Only the natural "client closed its input
        // stream" exit above has nothing left to drain; every other exit (cancel, error,
        // producer finish, even a disconnect mid-stream) can still have unread bytes queued.
        // Mirrors vgi-rpc-go's "Close output writer (sends EOS) ... Drain remaining input" and
        // vgi-rpc-java's closeStreamCleanly, both at this exact point.
        try
        {
            await outputWriter.WriteEosAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — the connection may already be broken, which the drain below will
            // discover on its own without needing this to have succeeded first.
        }

        if (inputNeedsDrain)
        {
            try
            {
                await inputReader.DrainRemainingBatchesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort — client already gone, or the stream was never fully opened.
            }
        }

        _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, streamHookError);
        await EmitAccessLogAsync(info.WireName, "stream", streamStatus, streamErrorType, streamErrorMessage, start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Builds and hands an <see cref="AccessLogRecord"/> to <see cref="_accessLog"/>, if one is
    /// configured. <paramref name="requestForLog"/> (unary calls only) is re-serialized as a
    /// self-contained Arrow IPC stream to satisfy access_log.schema.json's "unary requires
    /// request_data unless truncated" rule; <paramref name="streamId"/> (stream calls only)
    /// satisfies its "stream requires stream_id" rule. See docs/access-log-spec.md.
    /// </summary>
    private async Task EmitAccessLogAsync(
        string method,
        string methodType,
        string status,
        string errorType,
        string errorMessage,
        long startTimestamp,
        AnnotatedBatch? requestForLog = null,
        string? streamId = null,
        string? protocol = null,
        CancellationToken cancellationToken = default)
    {
        if (_accessLog is null)
        {
            return;
        }

        // A framework endpoint owned by no protocol (`__transport_options__`) logs the server's
        // primary -- what access-log-spec.md §3 prescribes for those, rather than a gap in it.
        var resolvedProtocol = protocol ?? ProtocolName;
        var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        string? requestData = null;
        string? truncated = null;
        long? originalRequestBytes = null;
        if (requestForLog is not null)
        {
            var raw = await SerializeForAccessLogAsync(requestForLog, cancellationToken).ConfigureAwait(false);
            if (_accessLog.IncludeRequestData)
            {
                requestData = Convert.ToBase64String(raw);
            }
            else
            {
                // base64 length is a pure function of the byte count — matches Python's
                // `4 * ((len(raw) + 2) // 3)` rather than paying to encode a payload nobody
                // asked to see at this log level.
                originalRequestBytes = 4L * ((raw.Length + 2) / 3);
                truncated = "payload_omitted";
            }
        }

        _accessLog.Write(new AccessLogRecord(
            Timestamp: DateTimeOffset.UtcNow,
            ServerId: _serverId,
            // A co-hosted framework protocol logs under its own name, not the application's:
            // a record saying an identity call happened on the application protocol would send
            // anyone auditing credential resolution to the wrong dashboard.
            //
            // The digest is derived from that same name rather than passed alongside it, so the
            // two cannot disagree. They did in the canonical Python implementation -- the name
            // was per-binding while the hash was the server's primary at every emit site, so a
            // reflection record named one protocol and carried another's digest. That is worse
            // than either field being wrong alone: access-log-spec.md §3 makes protocol_hash the
            // registry key for decoding archived records, so such a record is decoded against
            // the wrong description and nothing about it looks wrong. A call site here can get
            // the protocol wrong; it can no longer get the pair inconsistent.
            Protocol: resolvedProtocol,
            ProtocolHash: BindingHashFor(resolvedProtocol),
            Method: method,
            MethodType: methodType,
            Status: status,
            DurationMs: durationMs,
            ErrorType: errorType,
            ErrorMessage: string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
            ServerVersion: ServerVersion,
            StreamId: streamId,
            RequestData: requestData,
            Truncated: truncated,
            OriginalRequestBytes: originalRequestBytes));
    }

    /// <summary>
    /// Re-frames an already-read request <see cref="AnnotatedBatch"/> as a fresh, self-contained
    /// Arrow IPC stream (schema message, the one batch with its original custom_metadata, EOS) —
    /// what <c>pyarrow.ipc.open_stream</c> (and the conformance suite's access-log validator)
    /// requires. Mirrors Python's <c>_request_wire_bytes</c> fallback path (used there whenever
    /// the raw wire bytes aren't separately available, which for a shared pipe/socket stream is
    /// always).
    /// </summary>
    private static async Task<byte[]> SerializeForAccessLogAsync(AnnotatedBatch batch, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        await using (var writer = new WireWriter(ms, batch.Batch.Schema))
        {
            await writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return ms.ToArray();
    }

    private static Exception Unwrap(Exception exc) =>
        exc is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : exc;

    /// <summary>Forwards a stream turn's <see cref="ICallContext.EmitLog"/> calls into that
    /// turn's <see cref="OutputCollector"/> — matching Python's unified <c>ctx.emit_client_log</c>/
    /// <c>out.client_log()</c> (the same sink) during stream processing.</summary>
    private sealed class StreamCallContext(OutputCollector collector) : ICallContext
    {
        private readonly PeerConnectionIdentity _identity = PeerIdentityScope.Current;
        public AuthContext Auth => _identity.Auth;
        public PeerEvidenceSet PeerEvidence => _identity.Evidence;
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            collector.ClientLog(level, message, extra);
    }

    /// <summary>
    /// Buffers <see cref="ICallContext.EmitLog"/> calls made during a synchronous method body,
    /// flushed as zero-row log batches immediately before the result batch. Since a method body
    /// runs to completion before <see cref="ServeOneAsync"/> gets a chance to write anything,
    /// buffer-then-flush produces the same wire sequence true incremental interleaving would.
    /// </summary>
    private sealed class BufferedCallContext : ICallContext
    {
        private readonly PeerConnectionIdentity _identity = PeerIdentityScope.Current;
        public AuthContext Auth => _identity.Auth;
        public PeerEvidenceSet PeerEvidence => _identity.Evidence;
        public List<LogMessage> Buffered { get; } = [];

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            Buffered.Add(new LogMessage(level, message, extra));
    }

    private static readonly Schema s_emptySchema = new([], metadata: null);

    /// <summary>The empty method table a name this server does not host hashes over -- see
    /// <see cref="BindingHashFor"/>, whose callers may name one on an error path.</summary>
    private static readonly IReadOnlyDictionary<string, RpcMethodInfo> s_noMethods =
        new Dictionary<string, RpcMethodInfo>(StringComparer.Ordinal);

    private const string TransportOptionsMethodName = "__transport_options__";

    /// <summary>Introspection's retired predecessor, kept only so a stale caller can be told
    /// where introspection went.</summary>
    /// <remarks>
    /// A caller told merely "no such method" cannot tell "retired" from "this server was built
    /// without introspection", and those need opposite fixes -- update the client, or
    /// reconfigure the server. The C++ port spent real time on the first while reading an error
    /// describing the second.
    /// </remarks>
    internal const string RetiredDescribeMethodName = "__describe__";

    /// <summary>The refusal <see cref="RetiredDescribeMethodName"/> earns: fixable from the
    /// error text alone, without reading a changelog.</summary>
    /// <remarks>
    /// Both entry points are named because they are asked in order and answer different
    /// questions -- <c>list_protocols</c> for what this server hosts, then <c>describe</c> for
    /// one protocol's methods. The names are read off <see cref="ReflectionProtocol"/> rather
    /// than spelled, which the canonical Python implementation cannot do (its reflection module
    /// imports its server module, so the constant is duplicated there and pinned by a test); C#
    /// has no such cycle, so the message cannot drift from the protocol it points at.
    /// </remarks>
    internal static readonly string RetiredDescribeMessage =
        $"'{RetiredDescribeMethodName}' was retired. Introspection is now the "
        + $"'{ReflectionProtocol.ProtocolName}' protocol: call '{ReflectionProtocol.ListProtocolsMethod}' "
        + $"for what this server hosts, then '{ReflectionProtocol.DescribeMethod}' for one "
        + "protocol's methods.";

    /// <summary>Attaches to a client-advertised SHM segment named in <paramref name="request"/>'s
    /// own metadata (WIRE_PROTOCOL.md §11 "SHM segment identity in request metadata"), or returns
    /// <see langword="null"/> if none was advertised or the metadata is malformed/the segment
    /// can't be attached. Never throws — matches Python's <c>_maybe_attach_shm</c>: a caller that
    /// advertises a bad segment just gets treated as if it advertised none at all, since SHM is
    /// purely the caller's own optimization, never a contract this port enforces.</summary>
    /// <summary>Serve one call to <c>vgi_rpc.Reflection.v1</c>.</summary>
    /// <remarks>
    /// Two methods, deliberately. <c>list_protocols</c> is the cheap question -- what is here,
    /// and has it changed -- and the only one a client needs on a warm path, because the hash
    /// answers "has it changed" without transferring any schema. <c>describe</c> is the
    /// expensive one, asked once.
    ///
    /// <para>Self-description is not special-cased: reflection appears in its own output, so a
    /// client discovers it the same way it discovers everything else.</para>
    /// </remarks>
    private async Task<FrameworkDispatch> ServeReflectionAsync(
        Stream output, string methodName, AnnotatedBatch request, CancellationToken cancellationToken)
    {
        byte[] payload;
        if (methodName == ReflectionProtocol.ListProtocolsMethod)
        {
            // Every hosted protocol, in registration order -- which is how identity comes to
            // appear here without reflection knowing anything about it.
            var summaries = HostedProtocols
                .Select(name => new ReflectionProtocol.Summary(name, VersionForProtocol(name), BindingHashFor(name)))
                .ToList();
            payload = ReflectionProtocol.BuildProtocolList(
                _serverId, "", MetadataKeys.CurrentRequestVersion, summaries);
        }
        else if (methodName == ReflectionProtocol.DescribeMethod)
        {
            var requested = ReadProtocolArgument(request);
            var requestedMethods = MethodsForProtocol(requested);
            if (requestedMethods is null)
            {
                // Named, not silently empty: an empty description reads as
                // "this protocol has no methods". Typed, too -- "I do not speak that protocol"
                // carries its own error_kind, distinct from "I speak it but not that method",
                // and a client probing for an optional protocol depends on the difference.
                var unhosted = new ProtocolNotSupportedException(
                    $"This server does not host protocol '{requested}'. Hosted: [{string.Join(", ", HostedProtocols)}]");
                await WriteErrorStreamAsync(output, s_emptySchema, unhosted, cancellationToken).ConfigureAwait(false);
                return FrameworkDispatch.Failed(s_emptySchema, unhosted.GetType().Name, unhosted.Message);
            }

            payload = ReflectionProtocol.BuildServiceDescription(
                requested, VersionForProtocol(requested), BindingHashFor(requested), requestedMethods);
        }
        else
        {
            var unknown = new MethodNotImplementedException(
                $"Protocol '{ReflectionProtocol.ProtocolName}' has no method '{methodName}'. "
                + $"Available: [{string.Join(", ", ReflectionProtocol.MethodNames.OrderBy(k => k, StringComparer.Ordinal))}]");
            await WriteErrorStreamAsync(output, s_emptySchema, unknown, cancellationToken).ConfigureAwait(false);
            return FrameworkDispatch.Failed(s_emptySchema, unknown.GetType().Name, unknown.Message);
        }

        // The framework's ordinary convention for a structured return: the
        // payload rides as serialized bytes in a single `result` binary column.
        var schema = new Schema.Builder()
            .Field(new Field("result", BinaryType.Default, nullable: false))
            .Build();
        var builder = new BinaryArray.Builder();
        builder.Append((ReadOnlySpan<byte>)payload);
        var batch = new RecordBatch(schema, [builder.Build()], 1);
        var md = new Dictionary<string, string>
        {
            [MetadataKeys.ServerId] = _serverId,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        };
        await using (var writer = new WireWriter(output, schema))
        {
            await writer.WriteOwnedBatchAsync(batch, md, cancellationToken).ConfigureAwait(false);
        }

        return FrameworkDispatch.Ok(schema);
    }

    /// <summary>Serve one call to <c>vgi_rpc.Identity.v1</c>.</summary>
    /// <remarks>
    /// Self-contained rather than routed through the application dispatch path, exactly as
    /// <see cref="ServeReflectionAsync"/> is: a framework protocol is served by the framework, so
    /// nothing about hosting it can perturb what the application protocol puts on the wire.
    ///
    /// <para>Every guard lives in <see cref="IdentityImpl"/> and runs inside the invoke below.
    /// This method's only job is to get the caller's <see cref="AuthContext"/> in front of those
    /// guards and to put the answer -- or the refusal, carrying its <c>error_kind</c> -- back on
    /// the wire.</para>
    /// </remarks>
    private async Task<FrameworkDispatch> ServeIdentityAsync(
        Stream output, string methodName, AnnotatedBatch request, ICallContext callContext,
        bool emitAccessLog, CancellationToken cancellationToken)
    {
        if (!_identityMethods.TryGetValue(methodName, out var info))
        {
            // The narrowing made visible at dispatch: a method whose hook the deployment did not
            // configure is not hosted, so it is "no such method" rather than a refusal from a
            // method that exists.
            var available = string.Join(", ", _identityMethods.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var unknown = new MethodNotImplementedException(
                $"Protocol '{IdentityProtocol.ProtocolName}' has no method '{methodName}'. Available: [{available}]");
            await WriteErrorStreamAsync(output, s_emptySchema, unknown, cancellationToken).ConfigureAwait(false);
            return FrameworkDispatch.Failed(s_emptySchema, unknown.GetType().Name, unknown.Message);
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var status = "ok";
        var errorType = "";
        var errorMessage = "";
        Exception? hookError = null;
        var hookInfo = new DispatchHookInfo(info.WireName, "unary", IdentityProtocol.ProtocolName, _serverId);
        var hookToken = _dispatchHook?.OnDispatchStart(hookInfo);
        await using var writer = new WireWriter(output, info.ResultSchema);
        try
        {
            var args = ValueCodec.ExtractRow(request.Batch, info.ParameterTypes);
            var result = await info.InvokeAsync(_identity!, args, callContext).ConfigureAwait(false);
            var resultBatch = ValueCodec.BuildRow(info.ResultSchema, [result]);
            using var resultOwner = new RecordBatchOwner(resultBatch);
            await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            status = "error";
            errorType = actual.GetType().Name;
            errorMessage = actual.Message;
            hookError = actual;
            // LogMessage.FromException hoists an RpcException's ErrorKind to the top-level
            // vgi_rpc.error_kind metadata key. For this protocol that key is the whole
            // definitive-vs-transient signal a caller has, so it is not optional decoration.
            await writer.WriteOwnedBatchAsync(
                ValueCodec.EmptyRow(info.ResultSchema),
                LogMessage.FromException(actual).AddToMetadata(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, hookError);
            if (emitAccessLog)
            {
                await EmitAccessLogAsync(
                    info.WireName, "unary", status, errorType, errorMessage, start,
                    requestForLog: request,
                    protocol: IdentityProtocol.ProtocolName,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return new FrameworkDispatch(info.ResultSchema, status, errorType, errorMessage);
    }

    /// <summary>Whether <paramref name="methodName"/> is a framework built-in rather than a
    /// protocol method -- a <c>__dunder__</c> name, owned by the server and routed without a
    /// protocol.</summary>
    private static bool IsReservedMethodName(string methodName) =>
        methodName.Length > 4 && methodName.StartsWith("__", StringComparison.Ordinal)
        && methodName.EndsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// The refusal this request's <c>vgi_rpc.protocol</c> routing key earns, or
    /// <see langword="null"/> when it addresses the protocol this server hosts.
    /// </summary>
    /// <remarks>
    /// Reached only after the framework protocols have had their turn, so anything that still
    /// names one of them is naming a protocol this deployment did not configure -- which is
    /// "not hosted", not "no such method". The three answers stay distinct because a client
    /// probing for an optional protocol depends on the difference.
    /// </remarks>
    private RpcException? RoutingFailure(AnnotatedBatch request)
    {
        var declared = request.GetMetadata(MetadataKeys.Protocol);
        if (string.IsNullOrEmpty(declared))
        {
            return new ProtocolNotSpecifiedException(
                $"Request carries no '{MetadataKeys.Protocol}' routing key. Every request must name the "
                + $"protocol it addresses, including against a server hosting exactly one. This server "
                + $"hosts: [{string.Join(", ", HostedProtocols)}].");
        }

        if (!string.Equals(declared, ProtocolName, StringComparison.Ordinal))
        {
            return new ProtocolNotSupportedException(
                $"This server does not host protocol '{declared}'. Hosted: [{string.Join(", ", HostedProtocols)}].");
        }

        return null;
    }

    /// <summary>The version a hosted protocol declares, or "" when it declares none.</summary>
    /// <remarks>
    /// Only the application protocol can carry one here: the framework protocols are versioned by
    /// the major version in their own names, so an incompatible reflection or identity is a
    /// routing failure a client can act on rather than a mis-parse.
    /// </remarks>
    private string VersionForProtocol(string protocolName) =>
        protocolName == ProtocolName ? _expectedProtocolVersion ?? "" : "";

    /// <summary>The canonical protocol hash of one hosted protocol.</summary>
    /// <remarks>
    /// Computed on demand rather than at construction: <see cref="Hash.TypeTokens"/> refuses to
    /// spell an Arrow type it has no canonical token for, and that refusal belongs on the call
    /// that asked for a description, not on every server's startup path.
    /// </remarks>
    private string BindingHashFor(string protocolName)
    {
        var methods = MethodsForProtocol(protocolName);
        return methods is null
            ? ReflectionProtocol.BindingHash(protocolName, s_noMethods)
            : _bindingHashes.GetOrAdd(protocolName, name => ReflectionProtocol.BindingHash(name, methods));
    }

    /// <summary>Read the <c>protocol</c> argument off a <c>describe</c> request batch.</summary>
    private static string ReadProtocolArgument(AnnotatedBatch request)
    {
        var col = request.Batch.Column("protocol");
        if (col is not StringArray sa || sa.Length == 0 || sa.IsNull(0)) return "";
        return sa.GetString(0) ?? "";
    }

    /// <summary>The application-level protocol-version guard — see the constructor's
    /// <c>expectedProtocolVersion</c> doc comment. Returns <see langword="null"/> when the
    /// request's declared version shares its major AND minor with <paramref name="serverVersion"/>
    /// (patch is deliberately ignored), else a populated <see cref="ProtocolVersionException"/>
    /// ready to write back on the wire. Mirrors the canonical Python <c>_check_protocol_version</c>
    /// message shape exactly, including its four distinct "direction" phrasings, so cross-language
    /// error text stays recognizable regardless of which side authored the mismatch.</summary>
    private static ProtocolVersionException? CheckProtocolVersion(AnnotatedBatch request, string serverVersion)
    {
        var clientVersion = request.GetMetadata(MetadataKeys.ProtocolVersion);
        if (clientVersion is null)
        {
            return new ProtocolVersionException(FormatProtocolMismatch(
                null, serverVersion,
                "the client did not send a vgi_rpc.protocol_version metadata key. This is either a " +
                "vgi-rpc framework bug or a non-VGI client connecting to a VGI worker."));
        }

        if (TryParseSemver(clientVersion) is not { } clientParts)
        {
            return new ProtocolVersionException(FormatProtocolMismatch(
                clientVersion, serverVersion,
                "client sent a malformed protocol_version. Expected canonical semver MAJOR.MINOR.PATCH."));
        }

        // A malformed server-declared version is this process's own misconfiguration, not
        // something a client sent — that deserves a hard failure, not a wire-level RPC error.
        var serverParts = TryParseSemver(serverVersion)
            ?? throw new InvalidOperationException($"expectedProtocolVersion '{serverVersion}' is not valid semver.");

        if (clientParts.Major == serverParts.Major && clientParts.Minor == serverParts.Minor)
        {
            return null;
        }

        var clientIsOlder = clientParts.Major < serverParts.Major
            || (clientParts.Major == serverParts.Major && clientParts.Minor < serverParts.Minor);
        var direction = clientIsOlder
            ? $"client is too old; upgrade the VGI extension/client to a version supporting protocol_version {serverVersion}."
            : $"server is too old; upgrade the VGI worker to a version supporting protocol_version {clientVersion}.";

        return new ProtocolVersionException(FormatProtocolMismatch(clientVersion, serverVersion, direction));
    }

    private static string FormatProtocolMismatch(string? clientVersion, string serverVersion, string direction) =>
        $"VGI client/worker protocol_version mismatch.\n" +
        $"  Client: {clientVersion ?? "<not declared>"}\n" +
        $"  Server: {serverVersion}\n" +
        $"  Direction: {direction}";

    private static (int Major, int Minor, int Patch)? TryParseSemver(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
        {
            return null;
        }

        return (major, minor, patch);
    }

    private static ShmSegment? TryAttachShm(AnnotatedBatch request)
    {
        var name = request.GetMetadata(MetadataKeys.ShmSegmentName);
        if (name is null)
        {
            return null;
        }

        var sizeText = request.GetMetadata(MetadataKeys.ShmSegmentSize);
        if (sizeText is null || !long.TryParse(sizeText, out var size))
        {
            return null;
        }

        try
        {
            return ShmSegment.Attach(name, size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads and discards the client's tick/exchange input IPC stream (schema through EOS) for a
    /// stream call that ended before its lockstep loop began.
    /// </summary>
    /// <remarks>
    /// Best-effort, like the canonical Python server's own drain at the same point: a client
    /// that disconnects instead of sending the stream leaves nothing to keep in sync, and the
    /// serve loop's next read discovers the closed channel on its own.
    /// </remarks>
    private static async Task DrainAbandonedInputStreamAsync(IRpcTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            using var inputReader = new WireReader(transport.Input);
            _ = await inputReader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            await inputReader.DrainRemainingBatchesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client closed the channel rather than sending its input stream.
        }
    }

    private static async Task WriteErrorStreamAsync(Stream output, Schema schema, Exception exception, CancellationToken cancellationToken)
    {
        var metadata = LogMessage.FromException(exception).AddToMetadata();
        await using var writer = new WireWriter(output, schema);
        await writer.WriteOwnedBatchAsync(ValueCodec.EmptyRow(schema), metadata, cancellationToken).ConfigureAwait(false);
    }
}
