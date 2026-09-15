using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Hash;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// <c>vgi_rpc.Reflection.v1</c> -- discovery as an ordinary co-hosted protocol.
/// </summary>
/// <remarks>
/// <para>
/// Introspection used to be a hardcoded method name, <c>__describe__</c>, answered from a
/// pre-built batch before dispatch. That made it a thing every port had to hand-implement, in a
/// bespoke format, outside the machinery that serves every other method -- which is how the
/// ports drifted. Here it is a protocol like any other, addressed by the same routing key.
/// </para>
/// <para>
/// Following gRPC's reflection service and D-Bus's <c>org.freedesktop.DBus</c>, it is co-hosted
/// rather than special-cased. Its own major version sits in its name, so an incompatible
/// reflection is a routing failure a client can act on rather than a mis-parse.
/// </para>
/// <para>
/// Exempt from the <c>protocol_version</c> gate: this is the protocol a version-mismatched
/// client calls to learn <em>what</em> mismatched, and gating it would deny the client the
/// diagnosis it came for.
/// </para>
/// </remarks>
public static class ReflectionProtocol
{
    /// <summary>The wire name of the reflection protocol.</summary>
    /// <remarks>
    /// Fixed, and the one protocol name a client may know a priori: it is the bootstrap, so
    /// there is nothing to discover it with.
    /// </remarks>
    public const string ProtocolName = "vgi_rpc.Reflection.v1";

    /// <summary>The cheap question: what protocols are here, and have they changed.</summary>
    public const string ListProtocolsMethod = "list_protocols";

    /// <summary>The expensive question, asked once: the full surface of one protocol.</summary>
    public const string DescribeMethod = "describe";

    /// <summary>The two method names this protocol answers.</summary>
    /// <remarks>
    /// Deliberately not the same thing as <c>RpcServer.MethodsForProtocol(ProtocolName)</c>,
    /// which is empty: these methods are framework-owned rather than registered, so the honest
    /// hash is taken over an empty method table, while routing still has to know the two names
    /// exist. One is what the protocol <i>is</i>; the other is what it is <i>described as</i>.
    /// </remarks>
    public static readonly IReadOnlySet<string> MethodNames =
        new HashSet<string>([ListProtocolsMethod, DescribeMethod], StringComparer.Ordinal);

    /// <summary>The default <c>idempotency</c>: a caller must assume the worst.</summary>
    public const string IdempotencyUnknown = "unknown";

    /// <summary>The fallback when a stream method declares no kind.</summary>
    /// <remarks>
    /// This port decides per call, so a method that does not carry
    /// <see cref="Attributes.StreamKindAttribute"/> genuinely cannot be classified. "unknown" is
    /// the honest answer and is sayable, which is why the field is a string rather than a
    /// nullable boolean -- and why <c>is_exchange</c> is not in the protocol hash at all: which
    /// methods are undeclarable differs by port, so it cannot be a cross-language contract.
    /// </remarks>
    public const string StreamKindUnknown = "unknown";

    /// <summary>The stream kind to report for <paramref name="info"/>, or "" for a unary method.</summary>
    public static string StreamKindFor(RpcMethodInfo info)
    {
        if (info.Kind == RpcMethodKind.Unary) return "";
        return info.DeclaredStreamKind switch
        {
            Attributes.StreamKind.Producer => "producer",
            Attributes.StreamKind.Exchange => "exchange",
            _ => StreamKindUnknown,
        };
    }

    private static Field Utf8(string name) => new(name, StringType.Default, nullable: false);

    private static Field Bool(string name) => new(name, BooleanType.Default, nullable: false);

    private static Field Binary(string name) => new(name, BinaryType.Default, nullable: false);

    private static Field ListOf(string name, Field item) =>
        new(name, new ListType(item), nullable: false);

    private static Field StructOf(string name, IEnumerable<Field> children) =>
        new(name, new StructType(children.ToList()), nullable: true);

    /// <summary>The <c>ProtocolSummary</c> fields, mirroring the reference field for field.</summary>
    private static List<Field> ProtocolSummaryFields() =>
    [
        Utf8("protocol"),
        Utf8("protocol_version"),
        Utf8("protocol_hash"),
        Bool("deprecated"),
        Utf8("deprecation_message"),
        ListOf("features", new Field("item", StringType.Default, nullable: true)),
    ];

    /// <summary>The <c>MethodInfo</c> fields.</summary>
    private static List<Field> MethodInfoFields() =>
    [
        Utf8("name"),
        Utf8("method_type"),
        Bool("has_return"),
        Bool("has_header"),
        Utf8("stream_kind"),
        Binary("params_schema_ipc"),
        Binary("result_schema_ipc"),
        Binary("header_schema_ipc"),
        Utf8("idempotency"),
        Bool("deprecated"),
        Utf8("deprecation_message"),
    ];

    /// <summary>The <c>ProtocolList</c> payload schema.</summary>
    public static Schema ProtocolListSchema()
    {
        var b = new Schema.Builder();
        b.Field(Utf8("server_id"));
        b.Field(Utf8("server_version"));
        b.Field(Utf8("request_version"));
        b.Field(ListOf("protocols", StructOf("item", ProtocolSummaryFields())));
        return b.Build();
    }

    /// <summary>The <c>ServiceDescription</c> payload schema.</summary>
    /// <remarks>
    /// Carries no server identity: two processes serving the same protocol must describe it
    /// identically, or the description is not a property of the protocol. Server identity lives
    /// on <c>ProtocolList</c>, which is a statement about a server.
    /// </remarks>
    public static Schema ServiceDescriptionSchema()
    {
        var b = new Schema.Builder();
        foreach (var f in ProtocolSummaryFields()) b.Field(f);
        b.Field(ListOf("methods", StructOf("item", MethodInfoFields())));
        return b.Build();
    }

    /// <summary>Whether a method returns a value to its caller.</summary>
    /// <remarks>
    /// A stream's result schema is the (empty) protocol-level return, not something the caller
    /// receives, so the method kind is part of the question being asked.
    /// </remarks>
    public static bool UnaryHasReturn(RpcMethodInfo info) =>
        info.Kind == RpcMethodKind.Unary
        && info.ResultSchema is { FieldsList.Count: > 0 };

    /// <summary>The header schema this method declares, or null.</summary>
    public static Schema? HeaderSchemaOf(RpcMethodInfo info) =>
        info.HeaderClrType is null ? null : SchemaDerivation.InnerSchemaFor(info.HeaderClrType);

    /// <summary>One protocol's canonical fingerprint.</summary>
    public static string BindingHash(string name, IReadOnlyDictionary<string, RpcMethodInfo> methods)
    {
        var entries = methods.Values.Select(info => new ProtocolHash.HashMethod(
            info.WireName,
            info.Kind == RpcMethodKind.Unary ? "unary" : "stream",
            UnaryHasReturn(info),
            info.HeaderClrType is not null,
            info.ParamsSchema,
            info.ResultSchema,
            HeaderSchemaOf(info)));
        return ProtocolHash.ComputeProtocolHash(name, entries);
    }

    /// <summary>One hosted protocol, as it appears in <c>list_protocols</c>.</summary>
    public readonly record struct Summary(string Protocol, string Version, string Hash);

    /// <summary>Build the single-row <c>ProtocolList</c> batch, serialized as an IPC stream.</summary>
    public static byte[] BuildProtocolList(
        string serverId, string serverVersion, string requestVersion, IReadOnlyList<Summary> protocols)
    {
        var schema = ProtocolListSchema();
        var summaryFields = ProtocolSummaryFields();

        var structArray = BuildStructArray(
            summaryFields,
            protocols.Count,
            (i, name) => name switch
            {
                "protocol" => protocols[i].Protocol,
                "protocol_version" => protocols[i].Version,
                "protocol_hash" => protocols[i].Hash,
                "deprecation_message" => "",
                _ => null,
            },
            (_, name) => name == "deprecated" ? false : (bool?)null,
            (_, _) => null);

        var arrays = new IArrowArray[]
        {
            OneString(serverId),
            OneString(serverVersion),
            OneString(requestVersion),
            OneList(structArray, summaryFields),
        };
        return Serialize(schema, arrays);
    }

    /// <summary>Build the single-row <c>ServiceDescription</c> batch, serialized as an IPC stream.</summary>
    public static byte[] BuildServiceDescription(
        string protocol, string version, string hash, IReadOnlyDictionary<string, RpcMethodInfo> methods)
    {
        var schema = ServiceDescriptionSchema();
        var methodFields = MethodInfoFields();

        // Sorted so two ports iterating differently-ordered maps still agree.
        var ordered = methods.Values.OrderBy(m => m.WireName, StringComparer.Ordinal).ToList();

        var structArray = BuildStructArray(
            methodFields,
            ordered.Count,
            (i, name) => name switch
            {
                "name" => ordered[i].WireName,
                "method_type" => ordered[i].Kind == RpcMethodKind.Unary ? "unary" : "stream",
                "stream_kind" => StreamKindFor(ordered[i]),
                "idempotency" => IdempotencyUnknown,
                "deprecation_message" => "",
                _ => null,
            },
            (i, name) => name switch
            {
                "has_return" => UnaryHasReturn(ordered[i]),
                "has_header" => ordered[i].HeaderClrType is not null,
                "deprecated" => false,
                _ => null,
            },
            (i, name) => name switch
            {
                "params_schema_ipc" => SchemaIpc(ordered[i].ParamsSchema),
                // Empty rather than null when absent: a nullable column costs
                // every port a null check on a value it will only ever treat as
                // absent.
                "result_schema_ipc" => UnaryHasReturn(ordered[i]) ? SchemaIpc(ordered[i].ResultSchema) : [],
                "header_schema_ipc" => SchemaIpc(HeaderSchemaOf(ordered[i])),
                _ => null,
            });

        var arrays = new IArrowArray[]
        {
            OneString(protocol),
            OneString(version),
            OneString(hash),
            OneBool(false),
            OneString(""),
            // An empty but present feature list: additive capabilities announce
            // here rather than consuming version numbers.
            EmptyStringList(),
            OneList(structArray, methodFields),
        };
        return Serialize(schema, arrays);
    }

    private static byte[] SchemaIpc(Schema? schema)
    {
        if (schema is null) return [];
        using var ms = new MemoryStream();
        using (var w = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            w.WriteStart();
            w.WriteEnd();
        }
        // An IPC stream that carries only a schema still frames it as a full
        // stream, which is what every other port serializes and what the
        // reference's reader expects.
        return ms.ToArray();
    }

    private static StringArray OneString(string v)
    {
        var b = new StringArray.Builder();
        b.Append(v);
        return b.Build();
    }

    private static BooleanArray OneBool(bool v)
    {
        var b = new BooleanArray.Builder();
        b.Append(v);
        return b.Build();
    }

    private static ListArray EmptyStringList()
    {
        var b = new ListArray.Builder(new Field("item", StringType.Default, nullable: true));
        b.Append();
        return b.Build();
    }

    private static StructArray BuildStructArray(
        IReadOnlyList<Field> fields,
        int rows,
        Func<int, string, string?> str,
        Func<int, string, bool?> boolean,
        Func<int, string, byte[]?> binary)
    {
        var children = new List<IArrowArray>(fields.Count);
        foreach (var f in fields)
        {
            if (f.DataType is StringType)
            {
                var b = new StringArray.Builder();
                for (var i = 0; i < rows; i++) b.Append(str(i, f.Name) ?? "");
                children.Add(b.Build());
            }
            else if (f.DataType is BooleanType)
            {
                var b = new BooleanArray.Builder();
                for (var i = 0; i < rows; i++) b.Append(boolean(i, f.Name) ?? false);
                children.Add(b.Build());
            }
            else if (f.DataType is BinaryType)
            {
                var b = new BinaryArray.Builder();
                for (var i = 0; i < rows; i++) b.Append((ReadOnlySpan<byte>)(binary(i, f.Name) ?? []));
                children.Add(b.Build());
            }
            else
            {
                // The only remaining child is the empty feature list.
                var b = new ListArray.Builder(new Field("item", StringType.Default, nullable: true));
                for (var i = 0; i < rows; i++) b.Append();
                children.Add(b.Build());
            }
        }
        var validity = new ArrowBuffer.BitmapBuilder();
        for (var i = 0; i < rows; i++) validity.Append(true);
        return new StructArray(new StructType(fields.ToList()), rows, children, validity.Build(), 0);
    }

    private static ListArray OneList(StructArray values, IReadOnlyList<Field> fields)
    {
        var offsets = new ArrowBuffer.Builder<int>();
        offsets.Append(0);
        offsets.Append(values.Length);
        var validity = new ArrowBuffer.BitmapBuilder();
        validity.Append(true);
        var itemField = new Field("item", new StructType(fields.ToList()), nullable: true);
        return new ListArray(new ListType(itemField), 1, offsets.Build(), values, validity.Build(), 0);
    }

    private static byte[] Serialize(Schema schema, IArrowArray[] arrays)
    {
        using var batch = new RecordBatch(schema, arrays, 1);
        using var ms = new MemoryStream();
        using (var w = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            w.WriteRecordBatch(batch);
            w.WriteEnd();
        }
        return ms.ToArray();
    }
}
