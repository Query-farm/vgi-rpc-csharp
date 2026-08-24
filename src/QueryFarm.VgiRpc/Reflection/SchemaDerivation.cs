using System.Collections;
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
/// Dictionary&lt;K,V&gt;→map(K,V), Nullable&lt;T&gt;/a nullable reference→a nullable field,
/// and any other class/record→struct (fields from its public instance properties) — this repo
/// follows the Java port's precedent of struct-for-nested-records rather than Python's
/// embedded-IPC-stream-as-binary encoding, since a native Arrow struct column round-trips
/// through every Arrow implementation without needing a nested IPC parse.
/// </summary>
public static class SchemaDerivation
{
    private static readonly ConcurrentDictionary<Type, IArrowType> s_structTypeCache = new();

    /// <summary>Builds the Arrow field for a CLR type under the given wire name.</summary>
    public static Field FieldFor(string wireName, Type clrType) =>
        new(wireName, ArrowTypeFor(clrType, out var nullable), nullable);

    /// <summary>
    /// Resolves the Arrow type for a CLR type, unwrapping <see cref="Nullable{T}"/> and
    /// reporting whether the field should be nullable on the wire.
    /// </summary>
    public static IArrowType ArrowTypeFor(Type clrType, out bool nullable)
    {
        if (Nullable.GetUnderlyingType(clrType) is { } underlying)
        {
            nullable = true;
            return ArrowTypeForNonNullable(underlying);
        }

        // Reference types default to nullable on the wire unless a caller has independently
        // established (e.g. via NullabilityInfoContext on the originating member) that they
        // aren't — this overload doesn't have access to that context, so it errs permissive.
        nullable = clrType.IsClass || clrType.IsInterface;
        return ArrowTypeForNonNullable(clrType);
    }

    /// <summary>
    /// Resolves nullability from a parameter's actual nullable-reference-type annotation (via
    /// <see cref="NullabilityInfoContext"/>) rather than guessing from "is a reference type".
    /// </summary>
    public static Field FieldForParameter(string wireName, ParameterInfo parameter)
    {
        var context = new NullabilityInfoContext();
        var info = context.Create(parameter);
        var clrType = parameter.ParameterType;
        if (Nullable.GetUnderlyingType(clrType) is { } underlying)
        {
            return new Field(wireName, ArrowTypeForNonNullable(underlying), nullable: true);
        }

        var nullable = info.WriteState is NullabilityState.Nullable || !clrType.IsValueType && info.WriteState != NullabilityState.NotNull;
        return new Field(wireName, ArrowTypeForNonNullable(clrType), nullable);
    }

    /// <summary>Same as <see cref="FieldForParameter"/>, for a property (used for nested struct fields).</summary>
    public static Field FieldForProperty(string wireName, PropertyInfo property)
    {
        var context = new NullabilityInfoContext();
        var info = context.Create(property);
        var clrType = property.PropertyType;
        if (Nullable.GetUnderlyingType(clrType) is { } underlying)
        {
            return new Field(wireName, ArrowTypeForNonNullable(underlying), nullable: true);
        }

        var nullable = info.WriteState is NullabilityState.Nullable || !clrType.IsValueType && info.WriteState != NullabilityState.NotNull;
        return new Field(wireName, ArrowTypeForNonNullable(clrType), nullable);
    }

    private static IArrowType ArrowTypeForNonNullable(Type type)
    {
        if (type == typeof(string))
        {
            return StringType.Default;
        }

        if (type == typeof(byte[]))
        {
            return BinaryType.Default;
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

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new TimestampType(TimeUnit.Microsecond, "UTC");
        }

        if (type == typeof(DateOnly))
        {
            return Date32Type.Default;
        }

        if (type == typeof(TimeSpan))
        {
            return DurationType.Microsecond;
        }

        if (type == typeof(decimal))
        {
            return new Decimal128Type(38, 18);
        }

        if (type.IsEnum)
        {
            return new DictionaryType(Int16Type.Default, StringType.Default, ordered: false);
        }

        if (TryGetElementType(type, out var elementType))
        {
            return new ListType(FieldFor("item", elementType));
        }

        if (TryGetMapTypes(type, out var keyType, out var valueType))
        {
            var valueField = new Field("value", ArrowTypeFor(valueType, out var valueNullable), valueNullable);
            return new MapType(FieldFor("key", keyType), valueField);
        }

        return StructTypeFor(type);
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

    /// <summary>Struct type for a nested record/class, from its public instance properties, cached per type.</summary>
    private static IArrowType StructTypeFor(Type type) =>
        s_structTypeCache.GetOrAdd(type, static t =>
        {
            var fields = t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Select(p => FieldForProperty(WireNaming.ForProperty(p), p))
                .ToArray();
            return new StructType(fields);
        });
}
