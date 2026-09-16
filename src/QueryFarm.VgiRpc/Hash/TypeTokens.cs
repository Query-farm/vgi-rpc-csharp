using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace QueryFarm.VgiRpc.Hash;

/// <summary>
/// Canonical text tokens for Arrow types, for the protocol hash preimage.
/// </summary>
/// <remarks>
/// <para>
/// The protocol hash is taken over what Arrow <em>decodes to</em>, not over what an encoder
/// emits: each language's Arrow implementation may legitimately produce different bytes for the
/// same logical schema, so a hash over serialized IPC is not a cross-language contract. The
/// preimage is canonical JSON (RFC 8785) of the decoded description, and these tokens are how a
/// type appears inside it.
/// </para>
/// <para>
/// JSON solves framing, escaping and key ordering. It does not solve spelling -- two ports can
/// agree on every JCS rule and still disagree on whether a microsecond timestamp is
/// <c>timestamp[us]</c> or <c>timestamp(us)</c>, which is a silent hash divergence. So the
/// vocabulary is enumerated exhaustively and <see cref="TypeToken"/> is total: an unrecognised
/// type throws rather than falling back to the Arrow library's own <c>ToString</c>, whose output
/// is an implementation detail that differs from every other port.
/// </para>
/// <para>
/// <b>Grammar.</b> A token is lowercase ASCII. Parameters go in parentheses, children in angle
/// brackets. A child is <c>name:token</c> when non-nullable and <c>name?:token</c> when
/// nullable -- child nullability is part of the type in Arrow, and two schemas differing only
/// there are different schemas. Numeric parameters are folded into the token
/// (<c>decimal128(38,9)</c>) so the preimage contains no JSON numbers and RFC 8785's hardest
/// rule, number canonicalisation, never applies. Keep it that way.
/// </para>
/// <para>
/// <b>What is normalised.</b> Arrow's own type equality ignores the <em>name</em> of a list's
/// child field and of a map's key/value fields. Those names are normalised, because keeping them
/// would give two ports different hashes for a protocol Arrow itself calls identical. Everything
/// Arrow does treat as part of the type is kept: child nullability, struct field names, union
/// child names and type codes, dictionary index/value types and orderedness, and map
/// <c>keysSorted</c>.
/// </para>
/// </remarks>
public static class TypeTokens
{
    /// <summary>An Arrow type with no canonical token.</summary>
    /// <remarks>
    /// Thrown rather than falling back to <c>ToString</c>: a port that silently spelled an
    /// unknown type its own way would produce a protocol hash that disagrees with every other
    /// port, and the disagreement would surface as an unexplained mismatch at a client rather
    /// than as an error here.
    /// </remarks>
    public sealed class UnsupportedArrowTypeException : Exception
    {
        public UnsupportedArrowTypeException(object type)
            : base(
                $"Arrow type {type} has no canonical token. Add one to TypeTokens.cs and to every "
                    + "other port at the same time: a one-sided addition changes only this port's "
                    + "protocol hash.")
        { }
    }

    /// <summary>One top-level schema field as it appears in the hash preimage.</summary>
    public readonly record struct FieldToken(string Name, bool Nullable, string Type);

    /// <summary>Arrow's own spelling of a time unit.</summary>
    private static string UnitToken(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => "s",
        TimeUnit.Millisecond => "ms",
        TimeUnit.Microsecond => "us",
        TimeUnit.Nanosecond => "ns",
        _ => throw new UnsupportedArrowTypeException($"time unit {unit}"),
    };

    /// <summary>
    /// Spell a child whose name Arrow does not consider part of the type.
    /// </summary>
    /// <remarks>
    /// A list's child is named differently by each producer, and Arrow's own type equality
    /// ignores all of it. Normalising to a fixed name is what keeps two ports that default
    /// differently from hashing the same protocol differently. Nullability <em>is</em> part of
    /// the type, so it is kept.
    /// </remarks>
    private static string AnonChild(Field field, string name) =>
        $"{name}{(field.IsNullable ? "?" : "")}:{TypeToken(field)}";

    /// <summary>Spell a child field whose name is part of the type.</summary>
    private static string Child(Field field) => AnonChild(field, field.Name);

    /// <summary>Return the canonical token for <paramref name="field"/>'s type.</summary>
    public static string TypeToken(Field field)
    {
        var t = field.DataType;
        return t switch
        {
            NullType => "null",
            BooleanType => "bool",
            Int8Type => "int8",
            Int16Type => "int16",
            Int32Type => "int32",
            Int64Type => "int64",
            UInt8Type => "uint8",
            UInt16Type => "uint16",
            UInt32Type => "uint32",
            UInt64Type => "uint64",
            HalfFloatType => "float16",
            FloatType => "float32",
            DoubleType => "float64",
            StringType => "utf8",
            LargeStringType => "large_utf8",
            BinaryType => "binary",
            LargeBinaryType => "large_binary",
            // Decimal128Type and Decimal256Type both derive from
            // FixedSizeBinaryType in this Arrow binding, so they have to be
            // matched first or the base arm swallows them silently -- which
            // would spell a decimal as fixed_size_binary(16) and disagree with
            // every other port.
            Decimal128Type dec128 => $"decimal128({dec128.Precision},{dec128.Scale})",
            Decimal256Type dec256 => $"decimal256({dec256.Precision},{dec256.Scale})",
            FixedSizeBinaryType fsb => $"fixed_size_binary({fsb.ByteWidth})",
            Date32Type => "date32",
            Date64Type => "date64",
            Time32Type t32 => $"time32({UnitToken(t32.Unit)})",
            Time64Type t64 => $"time64({UnitToken(t64.Unit)})",
            // The zone is carried verbatim: "UTC" and "+00:00" are distinct
            // Arrow types and must not collapse to one token.
            TimestampType ts => string.IsNullOrEmpty(ts.Timezone)
                ? $"timestamp({UnitToken(ts.Unit)})"
                : $"timestamp({UnitToken(ts.Unit)},tz={ts.Timezone})",
            DurationType d => $"duration({UnitToken(d.Unit)})",
            IntervalType iv => iv.Unit switch
            {
                IntervalUnit.YearMonth => "interval_months",
                IntervalUnit.DayTime => "interval_day_time",
                IntervalUnit.MonthDayNanosecond => "interval_month_day_nano",
                _ => throw new UnsupportedArrowTypeException(t),
            },
            ListType list => $"list<{AnonChild(list.ValueField, "item")}>",
            FixedSizeListType fsl =>
                $"fixed_size_list({fsl.ListSize})<{AnonChild(fsl.ValueField, "item")}>",
            StructType st => $"struct<{string.Join(",", st.Fields.Select(Child))}>",
            // A map's child is a struct of the key and value fields. keysSorted
            // is part of the type in Arrow, so it is part of the token.
            MapType map => MapToken(map),
            DictionaryType dict =>
                $"dictionary<index:{TypeToken(new Field("i", dict.IndexType, false))},"
                    + $"value:{TypeToken(new Field("v", dict.ValueType, false))}>"
                    + (dict.Ordered ? ",ordered" : ""),
            UnionType union => UnionToken(union),
            _ => throw new UnsupportedArrowTypeException(t),
        };
    }

    private static string MapToken(MapType map)
    {
        var token = $"map<{AnonChild(map.KeyField, "key")},{AnonChild(map.ValueField, "value")}>";
        return map.KeySorted ? token + ",keys_sorted" : token;
    }

    private static string UnionToken(UnionType union)
    {
        // Type codes need not be 0..n-1, so they are spelled rather than
        // implied by position.
        var kind = union.Mode == UnionMode.Sparse ? "sparse_union" : "dense_union";
        var parts = new List<string>(union.Fields.Count);
        for (var i = 0; i < union.Fields.Count; i++)
        {
            var code = union.TypeIds is { Length: > 0 } ids && i < ids.Length ? ids[i] : i;
            parts.Add($"{code}={Child(union.Fields[i])}");
        }
        return $"{kind}<{string.Join(",", parts)}>";
    }

    /// <summary>Describe a schema's fields in declaration order, which is significant.</summary>
    public static List<FieldToken> SchemaTokens(Schema? schema)
    {
        var result = new List<FieldToken>();
        if (schema is null) return result;
        foreach (var f in schema.FieldsList)
        {
            result.Add(new FieldToken(f.Name, f.IsNullable, TypeToken(f)));
        }
        return result;
    }
}
