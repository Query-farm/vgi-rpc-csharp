using System.Net;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>One protocol a server hosts, as <c>vgi_rpc.Reflection.v1</c> lists it.</summary>
/// <remarks>
/// <para>
/// A client-side view of the wire <c>ProtocolSummary</c>, returned by
/// <see cref="RpcReflection.ListProtocolsAsync(object, CancellationToken)"/> in the server's
/// order: application protocols in registration order (the primary first), then the framework's
/// own (<c>vgi_rpc.Reflection.v1</c>, and <c>vgi_rpc.Identity.v1</c> on a server that hosts it).
/// </para>
/// <para>
/// The reference calls this <c>HostedProtocol</c>. This port already has a public
/// <see cref="Server.HostedProtocol"/> -- the server-side registration a host passes to
/// <c>RpcServer</c> -- and a file that serves and calls would see two types of the same name, so
/// the client-side view carries an <c>Info</c> suffix.
/// </para>
/// </remarks>
/// <param name="Name">The protocol's wire name -- its routing key, carrying its major version,
/// e.g. <c>vgi_rpc.Reflection.v1</c>.</param>
/// <param name="Version">Its declared semver, or <c>""</c> when it declares none.</param>
/// <param name="Hash">SHA-256 of its canonical description, as 64 lowercase hex characters.
/// Equal hashes mean an identical wire surface, in any port, so a caller holding a cached
/// description for this hash can skip
/// <see cref="RpcReflection.DescribeProtocolAsync(object, string, CancellationToken)"/>.</param>
/// <param name="Deprecated">Whether callers should migrate off this protocol.</param>
/// <param name="DeprecationMessage">What to migrate to; empty unless <paramref name="Deprecated"/>.</param>
public sealed record HostedProtocolInfo(
    string Name,
    string Version,
    string Hash,
    bool Deprecated = false,
    string DeprecationMessage = "")
{
    /// <summary>Capability tokens the protocol announces; empty when it announces none.</summary>
    public IReadOnlyList<string> Features { get; init; } = [];
}

/// <summary>One method of a described protocol.</summary>
/// <param name="Name">The method's wire name.</param>
/// <param name="MethodType">Unary or stream.</param>
/// <param name="HasReturn">Whether a unary method returns a value to its caller.</param>
/// <param name="ParamsSchema">The request parameter schema.</param>
/// <param name="ResultSchema">The unary result schema; empty for a stream (its output schema is
/// decided at run time by the implementation, so it cannot be reported statically).</param>
/// <param name="HasHeader">Whether a stream method declares a header.</param>
/// <param name="HeaderSchema">The header schema, or <see langword="null"/> without one.</param>
/// <param name="IsExchange">For a stream: <see langword="true"/> for exchange,
/// <see langword="false"/> for producer, <see langword="null"/> when the server cannot say.
/// Always <see langword="null"/> for a unary method.</param>
public sealed record MethodDescription(
    string Name,
    RpcMethodKind MethodType,
    bool HasReturn,
    Schema ParamsSchema,
    Schema ResultSchema,
    bool HasHeader = false,
    Schema? HeaderSchema = null,
    bool? IsExchange = null);

/// <summary>A protocol's full surface, from
/// <see cref="RpcReflection.DescribeProtocolAsync(object, string, CancellationToken)"/>.</summary>
/// <param name="ProtocolName">The protocol's wire name.</param>
/// <param name="RequestVersion">The server's wire request version.</param>
/// <param name="DescribeVersion">The introspection format version (<c>"5"</c>, vestigial since
/// reflection became a protocol).</param>
/// <param name="ProtocolHash">The protocol's canonical hash, as the listing reports it.</param>
/// <param name="ServerId">The answering server's instance identifier.</param>
/// <param name="Methods">Every method, keyed by wire name.</param>
/// <param name="ProtocolVersion">The protocol's declared semver, or <c>""</c>.</param>
public sealed record ServiceDescription(
    string ProtocolName,
    string RequestVersion,
    string DescribeVersion,
    string ProtocolHash,
    string ServerId,
    IReadOnlyDictionary<string, MethodDescription> Methods,
    string ProtocolVersion = "");

/// <summary>The server does not host <c>vgi_rpc.Reflection.v1</c>.</summary>
/// <remarks>
/// <para>
/// Thrown by <see cref="RpcReflection.ListProtocolsAsync(object, CancellationToken)"/> and
/// <see cref="RpcReflection.DescribeProtocolAsync(object, string, CancellationToken)"/> when the
/// server answers the reflection call with "not hosted" rather than with a listing: a server
/// that hosts it only on request and was not asked (the Python reference's
/// <c>enable_describe</c> defaults to off; this port's <c>RpcServer</c> always hosts it), or one
/// that predates reflection. Such a server still serves its own protocol, so this is a statement
/// about discovery, not about the connection -- the connection remains usable.
/// </para>
/// <para>
/// An <see cref="RpcException"/> carrying the server's original fields, so code that already
/// catches <see cref="RpcException"/> keeps working; catch this type to branch on "cannot
/// discover" specifically. No listing is ever inferred: only the caller knows which protocol it
/// expected.
/// </para>
/// </remarks>
public sealed class ReflectionNotSupportedException : RpcException
{
    /// <summary>Wraps the server's "not hosted" answer, keeping every field.</summary>
    public ReflectionNotSupportedException(RpcException error)
        : base(
            (error ?? throw new ArgumentNullException(nameof(error))).ErrorType,
            error.ErrorMessage,
            error.RemoteTraceback,
            error.RequestId,
            error.ErrorKind,
            error.ErrorCode,
            error.ErrorDetails)
    {
    }

    /// <summary>For an HTTP server older than protocol-scoped routes, which answers a bare 404
    /// with no Arrow error body to carry fields.</summary>
    internal ReflectionNotSupportedException(string message)
        : base("HttpError", message)
    {
    }
}

/// <summary>A client that can address one unary call to a protocol other than its own.</summary>
/// <remarks>
/// The rebind hook reflection rides on. Internal on purpose: the public route is
/// <see cref="RpcReflection"/>, and both implementers already expose their
/// <c>CallUnaryOnAsync</c> publicly.
/// </remarks>
internal interface IProtocolAddressableClient
{
    Task<AnnotatedBatch> CallUnaryOnAsync(
        string protocol,
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Something a caller holds that wraps a client: a typed proxy, an
/// <see cref="RpcConnection{TContract}"/>, a pool lease, an HTTP session scope.</summary>
internal interface IRpcClientHolder
{
    IRpcClient HeldClient { get; }
}

/// <summary>
/// Lists and describes the protocols a server hosts, over a connection the caller already holds.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes to <c>vgi_rpc.Reflection.v1</c> on the target's own connection: over HTTP it
/// shares the client's <see cref="System.Net.Http.HttpClient"/>, prefix, auth and session
/// settings; over every byte-stream transport (pipe, subprocess, worker pool, Unix, TCP, named
/// pipe, shared memory, Iroh) it shares the byte stream, which the server demultiplexes by each
/// request's protocol key. Nothing new is opened and nothing is closed. The target may be bound
/// to any protocol the server hosts.
/// </para>
/// <para>
/// Servers host reflection by default in this port. The Python reference hosts it only with
/// <c>enable_describe=True</c> (its conformance CLI's <c>--describe</c>); against one without
/// it, these methods throw <see cref="ReflectionNotSupportedException"/>.
/// </para>
/// </remarks>
public static class RpcReflection
{
    private const string DescribeVersion = "5";
    private const string ProtocolNotSupportedError = "ProtocolNotSupportedError";
    private const string MethodNotImplementedError = "MethodNotImplementedError";

    /// <summary>Lists the protocols the server hosts, in the server's order.</summary>
    /// <param name="target">An <see cref="RpcClient"/> or <c>HttpRpcClient</c>, a typed proxy
    /// from either's <c>CreateProxy</c>, an <see cref="RpcConnection{TContract}"/> or its proxy,
    /// a <see cref="WorkerPool.WorkerLease"/>, an <c>HttpSessionScope</c>, or a raw
    /// <see cref="IRpcTransport"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One <see cref="HostedProtocolInfo"/> per hosted protocol: application protocols
    /// first, primary leading, then the framework's own.</returns>
    /// <exception cref="ReflectionNotSupportedException">The server does not host reflection.
    /// The connection is still usable.</exception>
    /// <exception cref="RpcException">Any other server error, or a transport failure.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is none of the above.</exception>
    /// <remarks>
    /// One round trip. On a byte-stream transport, do not call it while a stream is open on the
    /// same connection: a connection carries one call at a time.
    /// </remarks>
    public static async Task<IReadOnlyList<HostedProtocolInfo>> ListProtocolsAsync(
        object target, CancellationToken cancellationToken = default)
    {
        var (caller, release) = Resolve(target);
        try
        {
            using var listing = await ListAsync(caller, cancellationToken).ConfigureAwait(false);
            return Summaries(listing);
        }
        finally
        {
            if (release is not null) await release.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Lists the protocols the server hosts; see
    /// <see cref="ListProtocolsAsync(object, CancellationToken)"/>.</summary>
    public static Task<IReadOnlyList<HostedProtocolInfo>> ListProtocolsAsync(
        this IRpcClient client, CancellationToken cancellationToken = default) =>
        ListProtocolsAsync((object)client, cancellationToken);

    /// <summary>Describes one hosted protocol.</summary>
    /// <param name="target">See <see cref="ListProtocolsAsync(object, CancellationToken)"/>.</param>
    /// <param name="name">The protocol's wire name, as the listing reports it.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>Its methods with their kinds and schemas.</returns>
    /// <exception cref="ReflectionNotSupportedException">The server does not host reflection.</exception>
    /// <exception cref="RpcException">The server does not host <paramref name="name"/>
    /// (<see cref="RpcException.ErrorKind"/> <c>protocol_not_supported</c>), answered with another
    /// error, or the transport failed.</exception>
    /// <remarks>
    /// Two round trips: <c>list_protocols</c> first -- for the server identity the description
    /// carries, and so that "no reflection" and "no such protocol" stay distinct, since
    /// <c>describe</c> answers <c>protocol_not_supported</c> for an unknown argument too -- then
    /// <c>describe</c>.
    /// </remarks>
    public static async Task<ServiceDescription> DescribeProtocolAsync(
        object target, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var (caller, release) = Resolve(target);
        try
        {
            using var listing = await ListAsync(caller, cancellationToken).ConfigureAwait(false);
            using var parameters = new RecordBatch(
                new Schema([new Field("protocol", StringType.Default, nullable: false)], null),
                [new StringArray.Builder().Append(name).Build()],
                1);
            using var described = await CallAsync(
                caller, ReflectionProtocol.DescribeMethod, parameters, cancellationToken).ConfigureAwait(false);
            return Description(described, listing);
        }
        finally
        {
            if (release is not null) await release.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Describes one hosted protocol; see
    /// <see cref="DescribeProtocolAsync(object, string, CancellationToken)"/>.</summary>
    public static Task<ServiceDescription> DescribeProtocolAsync(
        this IRpcClient client, string name, CancellationToken cancellationToken = default) =>
        DescribeProtocolAsync((object)client, name, cancellationToken);

    /// <summary>The caller to address reflection through, and what to release afterwards --
    /// only ever a wrapper this method made, never the target's connection.</summary>
    private static (IProtocolAddressableClient Caller, IAsyncDisposable? Release) Resolve(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // A holder may wrap another holder (a proxy over a lease's client), so unwrap fully.
        var current = target;
        for (var depth = 0; depth < 4 && current is IRpcClientHolder holder; depth++)
        {
            current = holder.HeldClient;
        }

        switch (current)
        {
            case IProtocolAddressableClient caller:
                return (caller, null);
            case IRpcTransport transport:
                // Borrowed, not owned: disposing the wrapper leaves the transport open.
                var wrapper = new RpcClient(
                    transport,
                    new RpcClientOptions { Protocol = ReflectionProtocol.ProtocolName },
                    ownsTransport: false);
                return (wrapper, wrapper);
            default:
                throw new ArgumentException(
                    $"Cannot reach reflection through a {target.GetType().Name}: pass an RpcClient or "
                    + "HttpRpcClient, a proxy from CreateProxy, an RpcConnection, a WorkerPool lease, an "
                    + "HttpSessionScope, or an IRpcTransport.",
                    nameof(target));
        }
    }

    private static async Task<RecordBatch> ListAsync(IProtocolAddressableClient caller, CancellationToken cancellationToken)
    {
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        try
        {
            return await CallAsync(caller, ReflectionProtocol.ListProtocolsMethod, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReflectionNotSupportedException)
        {
            throw;
        }
        catch (RpcException error) when (NotHosted(error))
        {
            throw new ReflectionNotSupportedException(error);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // An HTTP server older than protocol-scoped routes: a bare 404, no Arrow body.
            throw new ReflectionNotSupportedException($"HTTP 404: {error.Message}");
        }
    }

    /// <summary>Whether <paramref name="error"/>, answering <c>list_protocols</c>, says the server
    /// does not host reflection at all.</summary>
    /// <remarks>
    /// Only meaningful for <c>list_protocols</c>, which is always hosted when reflection is. A
    /// current server without reflection answers <c>protocol_not_supported</c>; one older than
    /// multi-protocol hosting ignores the protocol key and answers an unknown method; both carry
    /// <c>UNIMPLEMENTED</c> when the server sends a code at all.
    /// </remarks>
    internal static bool NotHosted(RpcException error) =>
        error.ErrorKind is MetadataKeys.ErrorKinds.ProtocolNotSupported or MetadataKeys.ErrorKinds.MethodNotImplemented
        || error.ErrorCode == ErrorCodes.Unimplemented
        || error.ErrorType is ProtocolNotSupportedError or MethodNotImplementedError;

    /// <summary>One reflection call, unwrapped from the <c>result</c> binary column to the
    /// single-row payload batch it carries.</summary>
    private static async Task<RecordBatch> CallAsync(
        IProtocolAddressableClient caller, string method, RecordBatch parameters, CancellationToken cancellationToken)
    {
        var reply = await caller.CallUnaryOnAsync(
            ReflectionProtocol.ProtocolName, method, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        using (reply.Batch)
        {
            if (reply.Batch.Schema.GetFieldIndex("result") < 0
                || reply.Batch.Column("result") is not BinaryArray result
                || result.Length == 0
                || result.IsNull(0))
            {
                throw new RpcException("ProtocolError", $"reflection '{method}' reply carries no 'result' payload.");
            }

            using var stream = new MemoryStream(result.GetBytes(0).ToArray());
            using var reader = new WireReader(stream);
            await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            var item = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new RpcException("ProtocolError", $"reflection '{method}' payload carried no batch.");
            return item.Batch;
        }
    }

    private static List<HostedProtocolInfo> Summaries(RecordBatch listing)
    {
        var (rows, start, end) = StructList(listing, "protocols");
        var name = Child<StringArray>(rows, "protocol");
        var version = Child<StringArray>(rows, "protocol_version");
        var hash = Child<StringArray>(rows, "protocol_hash");
        var deprecated = OptionalChild<BooleanArray>(rows, "deprecated");
        var message = OptionalChild<StringArray>(rows, "deprecation_message");
        var features = OptionalChild<ListArray>(rows, "features");
        var result = new List<HostedProtocolInfo>(end - start);
        for (var i = start; i < end; i++)
        {
            result.Add(new HostedProtocolInfo(
                name.GetString(i),
                version.GetString(i) ?? "",
                hash.GetString(i) ?? "",
                deprecated?.GetValue(i) ?? false,
                message?.GetString(i) ?? "")
            {
                Features = features is null ? [] : Strings(features, i),
            });
        }

        return result;
    }

    private static ServiceDescription Description(RecordBatch described, RecordBatch listing)
    {
        var (rows, start, end) = StructList(described, "methods");
        var name = Child<StringArray>(rows, "name");
        var methodType = Child<StringArray>(rows, "method_type");
        var hasReturn = Child<BooleanArray>(rows, "has_return");
        var hasHeader = Child<BooleanArray>(rows, "has_header");
        var streamKind = OptionalChild<StringArray>(rows, "stream_kind");
        var paramsIpc = Child<BinaryArray>(rows, "params_schema_ipc");
        var resultIpc = Child<BinaryArray>(rows, "result_schema_ipc");
        var headerIpc = Child<BinaryArray>(rows, "header_schema_ipc");
        var methods = new Dictionary<string, MethodDescription>(StringComparer.Ordinal);
        for (var i = start; i < end; i++)
        {
            var header = hasHeader.GetValue(i) == true;
            var method = new MethodDescription(
                name.GetString(i),
                methodType.GetString(i) == "unary" ? RpcMethodKind.Unary : RpcMethodKind.Stream,
                hasReturn.GetValue(i) == true,
                ReadSchema(paramsIpc, i),
                ReadSchema(resultIpc, i),
                header,
                header ? ReadSchema(headerIpc, i) : null,
                streamKind?.GetString(i) switch
                {
                    "exchange" => true,
                    "producer" => false,
                    _ => null,
                });
            methods[method.Name] = method;
        }

        return new ServiceDescription(
            Row0<StringArray>(described, "protocol").GetString(0),
            Row0<StringArray>(listing, "request_version").GetString(0) ?? "",
            DescribeVersion,
            Row0<StringArray>(described, "protocol_hash").GetString(0) ?? "",
            Row0<StringArray>(listing, "server_id").GetString(0) ?? "",
            methods,
            Row0<StringArray>(described, "protocol_version").GetString(0) ?? "");
    }

    private static Schema ReadSchema(BinaryArray column, int index)
    {
        if (column.IsNull(index)) return new Schema([], null);
        var bytes = column.GetBytes(index).ToArray();
        if (bytes.Length == 0) return new Schema([], null);
        using var reader = new ArrowStreamReader(new MemoryStream(bytes));
        return reader.Schema;
    }

    private static List<string> Strings(ListArray list, int index)
    {
        var values = (StringArray)list.Values;
        var result = new List<string>();
        if (list.IsNull(index)) return result;
        for (var j = list.ValueOffsets[index]; j < list.ValueOffsets[index + 1]; j++)
        {
            if (!values.IsNull(j)) result.Add(values.GetString(j));
        }

        return result;
    }

    private static T Row0<T>(RecordBatch batch, string column) where T : class, IArrowArray =>
        batch.Schema.GetFieldIndex(column) >= 0 && batch.Column(column) is T array && batch.Length > 0
            ? array
            : throw new RpcException("ProtocolError", $"reflection payload has no '{column}' column.");

    private static (StructArray Rows, int Start, int End) StructList(RecordBatch batch, string column)
    {
        var list = Row0<ListArray>(batch, column);
        var rows = list.Values as StructArray
            ?? throw new RpcException("ProtocolError", $"reflection payload '{column}' is not a list of structs.");
        return (rows, list.ValueOffsets[0], list.ValueOffsets[1]);
    }

    private static T Child<T>(StructArray rows, string field) where T : class, IArrowArray =>
        OptionalChild<T>(rows, field)
        ?? throw new RpcException("ProtocolError", $"reflection payload has no '{field}' field.");

    private static T? OptionalChild<T>(StructArray rows, string field) where T : class, IArrowArray
    {
        var index = ((StructType)rows.Data.DataType).GetFieldIndex(field);
        return index < 0 ? null : rows.Fields[index] as T;
    }
}
