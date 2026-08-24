using Apache.Arrow;
using Apache.Arrow.Types;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Encodes/decodes single CLR values to/from single-row Arrow arrays, per the type mapping in
/// <see cref="SchemaDerivation"/>.
///
/// <para><b>Milestone 1 scope note</b>: covers scalars (bool/all integer widths/float/double/
/// string/byte[]), <see cref="Nullable{T}"/>/nullable reference wrapping any of those,
/// <c>List&lt;T&gt;</c>/<c>T[]</c> of a supported scalar, and nested structs of supported
/// scalars. Enum (dictionary-encoded), <c>Dictionary&lt;K,V&gt;</c> (map), <c>HashSet&lt;T&gt;</c>,
/// and the temporal/decimal types are deferred to Milestone 2's conformance-service work — see
/// docs/roadmap.md — where they'll be built out against real conformance test vectors rather
/// than speculatively here.</para>
/// </summary>
public static class ValueCodec
{
    /// <summary>Builds a 1-row <see cref="RecordBatch"/> from CLR values, in <paramref name="schema"/> field order.</summary>
    public static RecordBatch BuildRow(Schema schema, IReadOnlyList<object?> values)
    {
        var arrays = new IArrowArray[schema.FieldsList.Count];
        for (var i = 0; i < arrays.Length; i++)
        {
            arrays[i] = BuildSingleValueArray(schema.GetFieldByIndex(i), values[i]);
        }

        return new RecordBatch(schema, arrays, length: 1);
    }

    /// <summary>Builds a zero-row batch for the given schema (used for log/error batches and void results).</summary>
    public static RecordBatch EmptyRow(Schema schema)
    {
        var arrays = schema.FieldsList.Select(f => BuildEmptyArray(f.DataType)).ToArray();
        return new RecordBatch(schema, arrays, length: 0);
    }

    /// <summary>Extracts row 0 of each column in <paramref name="batch"/>, decoded to the given CLR types, in order.</summary>
    public static object?[] ExtractRow(RecordBatch batch, IReadOnlyList<Type> clrTypes)
    {
        if (batch.Length == 0)
        {
            throw new InvalidOperationException("Expected a 1-row batch but got a zero-row batch.");
        }

        var values = new object?[clrTypes.Count];
        for (var i = 0; i < clrTypes.Count; i++)
        {
            values[i] = ExtractSingleValue(batch.Column(i), 0, clrTypes[i]);
        }

        return values;
    }

    private static IArrowArray BuildSingleValueArray(Field field, object? value)
    {
        if (value is null)
        {
            if (!field.IsNullable)
            {
                throw new ArgumentNullException(field.Name, $"Field '{field.Name}' is not nullable.");
            }

            return BuildEmptyArrayOrNull(field.DataType, nullRow: true);
        }

        return field.DataType switch
        {
            StringType => new StringArray.Builder().Append((string)value).Build(),
            BinaryType => new BinaryArray.Builder().Append((byte[])value).Build(),
            BooleanType => new BooleanArray.Builder().Append((bool)value).Build(),
            Int8Type => new Int8Array.Builder().Append((sbyte)value).Build(),
            UInt8Type => new UInt8Array.Builder().Append((byte)value).Build(),
            Int16Type => new Int16Array.Builder().Append((short)value).Build(),
            UInt16Type => new UInt16Array.Builder().Append((ushort)value).Build(),
            Int32Type => new Int32Array.Builder().Append((int)value).Build(),
            UInt32Type => new UInt32Array.Builder().Append((uint)value).Build(),
            Int64Type => new Int64Array.Builder().Append((long)value).Build(),
            UInt64Type => new UInt64Array.Builder().Append((ulong)value).Build(),
            FloatType => new FloatArray.Builder().Append((float)value).Build(),
            DoubleType => new DoubleArray.Builder().Append((double)value).Build(),
            ListType listType => BuildListArray(listType, (System.Collections.IEnumerable)value),
            StructType structType => BuildStructArray(structType, value),
            var other => throw NotSupportedYet(other),
        };
    }

    private static IArrowArray BuildEmptyArrayOrNull(IArrowType type, bool nullRow) =>
        type switch
        {
            StringType => nullRow ? new StringArray.Builder().AppendNull().Build() : new StringArray.Builder().Build(),
            BinaryType => nullRow ? new BinaryArray.Builder().AppendNull().Build() : new BinaryArray.Builder().Build(),
            BooleanType => nullRow ? new BooleanArray.Builder().AppendNull().Build() : new BooleanArray.Builder().Build(),
            Int8Type => nullRow ? new Int8Array.Builder().AppendNull().Build() : new Int8Array.Builder().Build(),
            UInt8Type => nullRow ? new UInt8Array.Builder().AppendNull().Build() : new UInt8Array.Builder().Build(),
            Int16Type => nullRow ? new Int16Array.Builder().AppendNull().Build() : new Int16Array.Builder().Build(),
            UInt16Type => nullRow ? new UInt16Array.Builder().AppendNull().Build() : new UInt16Array.Builder().Build(),
            Int32Type => nullRow ? new Int32Array.Builder().AppendNull().Build() : new Int32Array.Builder().Build(),
            UInt32Type => nullRow ? new UInt32Array.Builder().AppendNull().Build() : new UInt32Array.Builder().Build(),
            Int64Type => nullRow ? new Int64Array.Builder().AppendNull().Build() : new Int64Array.Builder().Build(),
            UInt64Type => nullRow ? new UInt64Array.Builder().AppendNull().Build() : new UInt64Array.Builder().Build(),
            FloatType => nullRow ? new FloatArray.Builder().AppendNull().Build() : new FloatArray.Builder().Build(),
            DoubleType => nullRow ? new DoubleArray.Builder().AppendNull().Build() : new DoubleArray.Builder().Build(),
            ListType listType => nullRow ? BuildListArray(listType, null) : BuildListArray(listType, System.Array.Empty<object>()),
            StructType structType => nullRow ? BuildStructArray(structType, null) : BuildStructArray(structType, EmptyEnumerable(structType)),
            var other => throw NotSupportedYet(other),
        };

    private static IArrowArray BuildEmptyArray(IArrowType type) => BuildEmptyArrayOrNull(type, nullRow: false);

    private static IEnumerable<object?> EmptyEnumerable(StructType _) => [];

    private static IArrowArray BuildListArray(ListType listType, System.Collections.IEnumerable? items)
    {
        var builder = new ListArray.Builder(listType.ValueField);
        if (items is null)
        {
            builder.AppendNull();
            return builder.Build();
        }

        builder.Append();
        foreach (var item in items)
        {
            AppendScalarToBuilder(builder.ValueBuilder, listType.ValueDataType, item);
        }

        return builder.Build();
    }

    private static IArrowArray BuildStructArray(StructType structType, object? value)
    {
        var childArrays = new IArrowArray[structType.Fields.Count];
        for (var i = 0; i < childArrays.Length; i++)
        {
            var field = structType.Fields[i];
            object? childValue = null;
            if (value is not null)
            {
                var property = value.GetType().GetProperty(FindClrPropertyName(value.GetType(), field));
                childValue = property?.GetValue(value);
            }

            childArrays[i] = value is null
                ? BuildEmptyArrayOrNull(field.DataType, nullRow: true)
                : BuildSingleValueArray(field, childValue);
        }

        var length = value is null ? 0 : 1;
        var nullCount = value is null ? 1 : 0;
        var validityBuffer = value is null
            ? Apache.Arrow.ArrowBuffer.Empty
            : new Apache.Arrow.ArrowBuffer.BitmapBuilder().Append(true).Build();
        var data = new ArrayData(structType, length, nullCount, 0, [validityBuffer], childArrays.Select(a => a.Data).ToArray());
        return new StructArray(data);
    }

    private static string FindClrPropertyName(Type clrType, Field wireField)
    {
        foreach (var property in clrType.GetProperties())
        {
            if (WireNaming.ForProperty(property) == wireField.Name)
            {
                return property.Name;
            }
        }

        throw new InvalidOperationException($"No property on '{clrType}' maps to wire field '{wireField.Name}'.");
    }

    private static void AppendScalarToBuilder(IArrowArrayBuilder<IArrowArray, IArrowArrayBuilder<IArrowArray>> builder, IArrowType elementType, object? value)
    {
        switch (elementType)
        {
            case StringType:
                var stringBuilder = (StringArray.Builder)builder;
                if (value is null) { stringBuilder.AppendNull(); } else { stringBuilder.Append((string)value); }
                break;
            case BinaryType:
                var binaryBuilder = (BinaryArray.Builder)builder;
                if (value is null) { binaryBuilder.AppendNull(); } else { binaryBuilder.Append((byte[])value); }
                break;
            case BooleanType:
                var boolBuilder = (BooleanArray.Builder)builder;
                if (value is null) { boolBuilder.AppendNull(); } else { boolBuilder.Append((bool)value); }
                break;
            case Int32Type:
                var int32Builder = (Int32Array.Builder)builder;
                if (value is null) { int32Builder.AppendNull(); } else { int32Builder.Append((int)value); }
                break;
            case Int64Type:
                var int64Builder = (Int64Array.Builder)builder;
                if (value is null) { int64Builder.AppendNull(); } else { int64Builder.Append((long)value); }
                break;
            case DoubleType:
                var doubleBuilder = (DoubleArray.Builder)builder;
                if (value is null) { doubleBuilder.AppendNull(); } else { doubleBuilder.Append((double)value); }
                break;
            case FloatType:
                var floatBuilder = (FloatArray.Builder)builder;
                if (value is null) { floatBuilder.AppendNull(); } else { floatBuilder.Append((float)value); }
                break;
            default:
                throw NotSupportedYet(elementType);
        }
    }

    private static object? ExtractSingleValue(IArrowArray array, int index, Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType);
        if (array.IsNull(index))
        {
            return underlying is not null || clrType.IsClass || clrType.IsInterface
                ? null
                : throw new InvalidOperationException($"Field is null but CLR type '{clrType}' isn't nullable.");
        }

        var effectiveType = underlying ?? clrType;

        return array switch
        {
            StringArray a => a.GetString(index),
            BinaryArray a => a.GetBytes(index).ToArray(),
            BooleanArray a => a.GetValue(index)!.Value,
            Int8Array a => a.Values[index],
            UInt8Array a => a.Values[index],
            Int16Array a => a.Values[index],
            UInt16Array a => a.Values[index],
            Int32Array a => a.Values[index],
            UInt32Array a => a.Values[index],
            Int64Array a => a.Values[index],
            UInt64Array a => a.Values[index],
            FloatArray a => a.Values[index],
            DoubleArray a => a.Values[index],
            ListArray a => ExtractList(a, index, effectiveType),
            StructArray a => ExtractStruct(a, index, effectiveType),
            _ => throw NotSupportedYet(array.Data.DataType),
        };
    }

    private static object ExtractList(ListArray array, int index, Type clrListType)
    {
        var elementType = clrListType.IsArray
            ? clrListType.GetElementType()!
            : clrListType.GetGenericArguments() is [var t] ? t : typeof(object);

        var start = array.ValueOffsets[index];
        var end = array.ValueOffsets[index + 1];
        var values = array.Values;
        var list = new List<object?>(end - start);
        for (var i = start; i < end; i++)
        {
            list.Add(ExtractSingleValue(values, i, elementType));
        }

        if (clrListType.IsArray)
        {
            var typedArray = System.Array.CreateInstance(elementType, list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                typedArray.SetValue(list[i], i);
            }

            return typedArray;
        }

        var listInstance = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in list)
        {
            listInstance.Add(item);
        }

        return listInstance;
    }

    private static object ExtractStruct(StructArray array, int index, Type clrType)
    {
        var instance = Activator.CreateInstance(clrType) ?? throw new InvalidOperationException($"Cannot construct '{clrType}' — it needs a public parameterless constructor.");
        var structType = (StructType)array.Data.DataType;
        for (var i = 0; i < structType.Fields.Count; i++)
        {
            var field = structType.Fields[i];
            var propertyName = FindClrPropertyName(clrType, field);
            var property = clrType.GetProperty(propertyName)!;
            var value = ExtractSingleValue(array.Fields[i], index, property.PropertyType);
            property.SetValue(instance, value);
        }

        return instance;
    }

    private static NotSupportedException NotSupportedYet(IArrowType type) =>
        new($"Arrow type '{type}' is not yet supported by ValueCodec — deferred to Milestone 2's conformance work. See docs/roadmap.md.");
}
