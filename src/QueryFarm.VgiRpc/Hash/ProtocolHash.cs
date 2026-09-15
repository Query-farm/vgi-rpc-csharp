using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;

namespace QueryFarm.VgiRpc.Hash;

/// <summary>
/// The protocol hash: a fingerprint of a protocol's wire surface.
/// </summary>
/// <remarks>
/// <para>
/// A client and a worker agree on a protocol or they do not, and the hash is how either side
/// says which one it has without shipping the whole description. For that to be worth anything
/// the same protocol must hash the same in every port, which the previous definition could not
/// promise: it hashed serialized Arrow IPC bytes, and each language's Arrow implementation may
/// legitimately emit different bytes for the same logical schema. The docs said so, which made
/// the field advisory -- comparable only against itself.
/// </para>
/// <para>
/// So the preimage is canonical JSON of what Arrow <em>decodes to</em>:
/// <c>sha256("vgi_rpc.protocol_hash.v1|" + canonicalJson(description))</c>.
/// </para>
/// <para>
/// Profile: RFC 8785 (JCS), chosen for its published test vectors. The structure is deliberately
/// restricted to objects, arrays, strings and booleans; every number is folded into a type token
/// (<c>decimal128(38,9)</c>), so JCS's hardest rule -- number canonicalisation, and the likeliest
/// place for six ports to diverge -- never applies. Keep it that way.
/// </para>
/// <para>
/// Not in the preimage: server identity, docstrings, parameter defaults, language-specific type
/// names, the framework's own request/describe versions, and whether a stream is an exchange.
/// That last is an <em>implementation</em> property, not visible on the protocol definition, so
/// one port can determine it and another cannot -- and a field one port knows and another does
/// not cannot be part of a cross-language contract. It still reaches clients as
/// <c>stream_kind</c> on the description, where "unknown" is a sayable answer; a hash has no
/// such option.
/// </para>
/// </remarks>
public static class ProtocolHash
{
    /// <summary>
    /// Domain separator. Moves only when the hash definition moves, never when a protocol
    /// changes -- that is what the hash itself is for.
    /// </summary>
    public const string HashDomain = "vgi_rpc.protocol_hash.v1|";

    /// <summary>One method's input.</summary>
    /// <remarks>
    /// Takes decoded schemas rather than serialized IPC: the hash is over structure, and
    /// accepting bytes would invite a caller to pass whatever its encoder produced.
    /// </remarks>
    public sealed record HashMethod(
        string Name,
        string MethodType,
        bool HasReturn,
        bool HasHeader,
        Schema? ParamsSchema,
        Schema? ResultSchema,
        Schema? HeaderSchema);

    /// <summary>Build the canonical preimage for one protocol.</summary>
    /// <remarks>
    /// Exposed because a hash mismatch between ports is otherwise one bit of information. With
    /// the preimage in hand a failing port diffs two JSON documents and sees which method, field
    /// or type token it spells differently.
    /// </remarks>
    public static string CanonicalDescription(string protocolName, IEnumerable<HashMethod> methods)
    {
        // Sorted so two ports iterating differently-ordered maps still agree.
        var sorted = methods.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder("{\"methods\":[");
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendMethod(sb, sorted[i]);
        }
        sb.Append("],\"protocol\":");
        AppendJsonString(sb, protocolName);
        return sb.Append('}').ToString();
    }

    /// <summary>One method entry, with its keys emitted in sorted order.</summary>
    /// <remarks>
    /// Canonical JSON requires sorted keys, and the order here is load bearing rather than
    /// cosmetic: emitting <c>header</c> after <c>params</c> would change the digest for every
    /// protocol that has a stream header, which is exactly the class of bug the canonical form
    /// exists to prevent.
    /// </remarks>
    private static void AppendMethod(StringBuilder sb, HashMethod m)
    {
        sb.Append("{\"has_header\":").Append(m.HasHeader ? "true" : "false");
        sb.Append(",\"has_return\":").Append(m.HasReturn ? "true" : "false");
        // Absent and empty are different: a method returning nothing is not a
        // method returning an empty struct, and they must not hash alike.
        if (m.HasHeader)
        {
            sb.Append(",\"header\":");
            AppendFieldTokens(sb, m.HeaderSchema);
        }
        sb.Append(",\"name\":");
        AppendJsonString(sb, m.Name);
        sb.Append(",\"params\":");
        AppendFieldTokens(sb, m.ParamsSchema);
        if (m.HasReturn)
        {
            sb.Append(",\"result\":");
            AppendFieldTokens(sb, m.ResultSchema);
        }
        sb.Append(",\"type\":");
        AppendJsonString(sb, m.MethodType);
        sb.Append('}');
    }

    private static void AppendFieldTokens(StringBuilder sb, Schema? schema)
    {
        var tokens = TypeTokens.SchemaTokens(schema);
        sb.Append('[');
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":");
            AppendJsonString(sb, tokens[i].Name);
            sb.Append(",\"nullable\":").Append(tokens[i].Nullable ? "true" : "false");
            sb.Append(",\"type\":");
            AppendJsonString(sb, tokens[i].Type);
            sb.Append('}');
        }
        sb.Append(']');
    }

    /// <summary>Encode a string the way RFC 8785 requires.</summary>
    /// <remarks>
    /// Short escapes where JCS mandates them, <c>\uXXXX</c> only for the remaining control
    /// characters, and every other character emitted as itself -- notably <em>not</em>
    /// ASCII-escaped, which is where a JSON library's defaults would silently diverge from the
    /// other ports.
    /// </remarks>
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Return the SHA-256 hex digest of a protocol's canonical description.</summary>
    /// <remarks>
    /// Identical in every port for the same protocol -- which is a property conformance can
    /// assert, and could not before.
    /// </remarks>
    public static string ComputeProtocolHash(string protocolName, IEnumerable<HashMethod> methods)
    {
        var preimage = Encoding.UTF8.GetBytes(HashDomain + CanonicalDescription(protocolName, methods));
        return Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
    }
}
