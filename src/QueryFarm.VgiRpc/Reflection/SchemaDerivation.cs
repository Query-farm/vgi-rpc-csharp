using System.Collections.Concurrent;
using System.Reflection;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Derives Arrow <see cref="Field"/>/<see cref="Schema"/> values from CLR types via reflection —
/// there is no IDL/codegen step; a service interface's parameter and return types ARE the
/// schema, exactly as in every other vgi-rpc port. See the type-mapping table in
/// WIRE_PROTOCOL.md §4 and docs/wire-protocol.md.
///
/// Type mapping: string→utf8, byte[]→binary, bool→bool, integer widths→matching Arrow int
/// width, float/double→float32/float64, enum→dictionary(int16,utf8) by member name (see
/// <see cref="WireNaming.ForEnumMember"/>), List&lt;T&gt;/T[]→list, HashSet&lt;T&gt;→list,
/// Dictionary&lt;K,V&gt;→map(K,V), Nullable&lt;T&gt;/a nullable reference→a nullable field.
///
/// A nested dataclass-equivalent is a two-tier rule, confirmed empirically against the real
/// Python reference client via <c>vgi-rpc-test</c> (a uniform native-Arrow-<c>struct</c>
/// encoding — which is what the Java port uses — does NOT round-trip against it):
/// <list type="bullet">
/// <item>At the TOP LEVEL (a service method's own parameter or return type) → <c>binary</c>
/// containing an embedded Arrow IPC stream (the dataclass's own schema + one row + EOS). See
/// <see cref="InnerSchemaFor"/> and <see cref="ValueCodec"/>'s embedded-record helpers.</item>
/// <item>NESTED inside another dataclass's own fields (i.e. once already inside an embedded
/// IPC stream's schema) → a native Arrow <c>struct</c> column — no need for a further nested
/// IPC stream when the enclosing schema can just describe it directly.</item>
/// </list>
/// </summary>
public static class SchemaDerivation
{
    private static readonly ConcurrentDictionary<Type, Schema> s_innerSchemaCache = new();
    private static readonly ConcurrentDictionary<Type, StructType> s_nestedStructTypeCache = new();

    /// <summary>Builds the top-level Arrow field for a CLR type under the given wire name.</summary>
    public static Field FieldFor(string wireName, Type clrType) => FieldFor(wireName, clrType, largeWidth: false);

    /// <summary>Same as <see cref="FieldFor(string, Type)"/>, forcing the 64-bit-offset variant
    /// for a <c>string</c>/<c>byte[]</c> type — see <see cref="LargeWidthAttribute"/>.</summary>
    public static Field FieldFor(string wireName, Type clrType, bool largeWidth) =>
        new(wireName, ArrowTypeFor(clrType, largeWidth, out var nullable), nullable);

    /// <summary>
    /// Resolves the top-level Arrow type for a CLR type, unwrapping <see cref="Nullable{T}"/>
    /// and reporting whether the field should be nullable on the wire.
    /// </summary>
    public static IArrowType ArrowTypeFor(Type clrType, out bool nullable) => ArrowTypeFor(clrType, largeWidth: false, out nullable);

    /// <summary>Same as <see cref="ArrowTypeFor(Type, out bool)"/>, forcing the 64-bit-offset
    /// variant for a <c>string</c>/<c>byte[]</c> type — see <see cref="LargeWidthAttribute"/>.</summary>
    public static IArrowType ArrowTypeFor(Type clrType, bool largeWidth, out bool nullable)
    {
        if (Nullable.GetUnderlyingType(clrType) is { } underlying)
        {
            nullable = true;
            return ArrowTypeForNonNullable(underlying, nested: false, largeWidth);
        }

        // Reference types default to nullable on the wire unless a caller has independently
        // established (e.g. via NullabilityInfoContext on the originating member) that they
        // aren't — this overload doesn't have access to that context, so it errs permissive.
        nullable = clrType.IsClass || clrType.IsInterface;
        return ArrowTypeForNonNullable(clrType, nested: false, largeWidth);
    }

    /// <summary>
    /// Resolves nullability from a parameter's actual nullable-reference-type annotation (via
    /// <see cref="NullabilityInfoContext"/>) rather than guessing from "is a reference type".
    /// Honors a <see cref="LargeWidthAttribute"/> on the parameter.
    /// </summary>
    public static Field FieldForParameter(string wireName, ParameterInfo parameter) =>
        FieldForMember(wireName, parameter.ParameterType, new NullabilityInfoContext().Create(parameter), nested: false, largeWidth: parameter.IsDefined(typeof(LargeWidthAttribute)));

    /// <summary>Same as <see cref="FieldForParameter"/>, for a property of a dataclass-equivalent's own fields.</summary>
    public static Field FieldForProperty(string wireName, PropertyInfo property) =>
        FieldForMember(wireName, property.PropertyType, new NullabilityInfoContext().Create(property), nested: true, largeWidth: false);

    private static Field FieldForMember(string wireName, Type clrType, NullabilityInfo info, bool nested, bool largeWidth)
    {
        if (Nullable.GetUnderlyingType(clrType) is { } underlying)
        {
            return new Field(wireName, ArrowTypeForNonNullable(underlying, nested, largeWidth), nullable: true);
        }

        var nullable = info.WriteState is NullabilityState.Nullable || !clrType.IsValueType && info.WriteState != NullabilityState.NotNull;
        return new Field(wireName, ArrowTypeForNonNullable(clrType, nested, largeWidth), nullable);
    }

    private static IArrowType ArrowTypeForNonNullable(Type type, bool nested, bool largeWidth = false)
    {
        if (type == typeof(string))
        {
            return largeWidth ? LargeStringType.Default : StringType.Default;
        }

        if (type == typeof(byte[]))
        {
            return largeWidth ? LargeBinaryType.Default : BinaryType.Default;
        }

        if (type == typeof(bool))
        {
            return BooleanType.Default;
        }

        if (type == typeof(sbyte))
        {
            return Int8Type.Default;
        }

        if (type == typeof(byte))
        {
            return UInt8Type.Default;
        }

        if (type == typeof(short))
        {
            return Int16Type.Default;
        }

        if (type == typeof(ushort))
        {
            return UInt16Type.Default;
        }

        if (type == typeof(int))
        {
            return Int32Type.Default;
        }

        if (type == typeof(uint))
        {
            return UInt32Type.Default;
        }

        if (type == typeof(long))
        {
            return Int64Type.Default;
        }

        if (type == typeof(ulong))
        {
            return UInt64Type.Default;
        }

        if (type == typeof(float))
        {
            return FloatType.Default;
        }

        if (type == typeof(double))
        {
            return DoubleType.Default;
        }

        // A naive DateTime carries no offset — mirrors Python's naive datetime.datetime, which
        // vgi-rpc's conformance protocol maps to pa.timestamp("us") with no tz. A DateTimeOffset
        // always has one, matching the UTC-tagged pa.timestamp("us", tz="UTC") case. Two distinct
        // CLR types for two distinct wire shapes, rather than one type doing double duty.
        if (type == typeof(DateTime))
        {
            return new TimestampType(TimeUnit.Microsecond, (string?)null);
        }

        if (type == typeof(DateTimeOffset))
        {
            return new TimestampType(TimeUnit.Microsecond, "UTC");
        }

        if (type == typeof(DateOnly))
        {
            return Date32Type.Default;
        }

        if (type == typeof(TimeOnly))
        {
            return new Time64Type(TimeUnit.Microsecond);
        }

        if (type == typeof(TimeSpan))
        {
            return DurationType.Microsecond;
        }

        if (type == typeof(decimal))
        {
            // (20, 4) matches the one wire shape the conformance protocol's echo_decimal
            // currently exercises (pa.decimal128(20, 4)) — not yet configurable per-field (that
            // needs an attribute-based override mechanism, deferred; see IConformanceService's
            // wide-Arrow-types TODO). Revisit if/when a second decimal shape is needed.
            return new Decimal128Type(20, 4);
        }

        if (type.IsEnum)
        {
            return new DictionaryType(Int16Type.Default, StringType.Default, ordered: false);
        }

        if (TryGetElementType(type, out var elementType))
        {
            // A collection element is always "nested" for the dataclass two-tier rule,
            // regardless of whether the collection itself is a top-level parameter/result: only
            // the TOP-LEVEL value gets the embedded-IPC-in-binary treatment; anything inside a
            // container is already native Arrow, so a dataclass element is a struct, not another
            // layer of embedded binary.
            return new ListType(ElementField("item", elementType, nested: true, forceNonNullable: false));
        }

        if (TryGetMapTypes(type, out var keyType, out var valueType))
        {
            // Map keys must be non-nullable (Arrow's own constraint) regardless of what a
            // reference-type key's default nullability would otherwise be.
            var keyField = ElementField("key", keyType, nested: true, forceNonNullable: true);
            var valueField = ElementField("value", valueType, nested: true, forceNonNullable: false);
            return new MapType(keyField, valueField);
        }

        // A nested dataclass-equivalent: see the two-tier rule in this class's doc comment.
        return nested ? NestedStructTypeFor(type) : BinaryType.Default;
    }

    /// <summary>A list/map element field — unwraps <see cref="Nullable{T}"/> (there's no
    /// <see cref="NullabilityInfoContext"/> source for a bare generic-argument type, so
    /// reference-type elements default to nullable, matching <see cref="ArrowTypeFor(Type, out bool)"/>'s
    /// top-level-parameter behavior).</summary>
    private static Field ElementField(string name, Type elementType, bool nested, bool forceNonNullable)
    {
        if (Nullable.GetUnderlyingType(elementType) is { } underlying)
        {
            return new Field(name, ArrowTypeForNonNullable(underlying, nested), nullable: !forceNonNullable);
        }

        var nullable = !forceNonNullable && (elementType.IsClass || elementType.IsInterface);
        return new Field(name, ArrowTypeForNonNullable(elementType, nested), nullable);
    }

    /// <summary>List/set element type: <c>T[]</c>, <c>List&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>,
    /// or <c>HashSet&lt;T&gt;</c>/<c>ISet&lt;T&gt;</c> (both map to Arrow's <c>list</c>, per spec).</summary>
    private static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type == typeof(string) || TryGetMapTypes(type, out _, out _))
        {
            elementType = typeof(object);
            return false;
        }

        foreach (var candidateInterface in GetInterfacesAndSelf(type))
        {
            if (candidateInterface.IsGenericType && candidateInterface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = candidateInterface.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = typeof(object);
        return false;
    }

    private static bool TryGetMapTypes(Type type, out Type keyType, out Type valueType)
    {
        foreach (var candidateInterface in GetInterfacesAndSelf(type))
        {
            if (candidateInterface.IsGenericType && candidateInterface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                var args = candidateInterface.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        keyType = typeof(object);
        valueType = typeof(object);
        return false;
    }

    private static IEnumerable<Type> GetInterfacesAndSelf(Type type)
    {
        if (type.IsInterface)
        {
            yield return type;
        }

        foreach (var i in type.GetInterfaces())
        {
            yield return i;
        }
    }

    /// <summary>
    /// The Arrow schema for a TOP-LEVEL dataclass-equivalent's own fields (its public instance
    /// properties) — used as the embedded IPC stream's schema when encoding/decoding it as a
    /// <c>binary</c> wire value. Fields that are themselves dataclasses resolve as nested
    /// <c>struct</c> (the "nested" tier of the two-tier rule). See <see cref="ValueCodec"/>'s
    /// embedded-record helpers.
    /// </summary>
    public static Schema InnerSchemaFor(Type type) =>
        s_innerSchemaCache.GetOrAdd(type, static t => new Schema(PropertyFields(t, nested: true), metadata: null));

    /// <summary>Native Arrow <c>struct</c> type for a dataclass nested inside another dataclass's own fields.</summary>
    private static StructType NestedStructTypeFor(Type type) =>
        s_nestedStructTypeCache.GetOrAdd(type, static t => new StructType(PropertyFields(t, nested: true)));

    private static Field[] PropertyFields(Type type, bool nested) =>
        type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => FieldForMember(WireNaming.ForProperty(p), p.PropertyType, new NullabilityInfoContext().Create(p), nested, largeWidth: false))
            .ToArray();
}
