using System.Reflection;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Encodes/decodes single CLR values to/from single-row Arrow arrays, per the type mapping in
/// <see cref="SchemaDerivation"/>.
///
/// <para>Covers scalars, <see cref="Nullable{T}"/>/nullable reference wrapping any of those,
/// <c>List&lt;T&gt;</c>/<c>T[]</c>/<c>HashSet&lt;T&gt;</c> (including nested lists, list-of-struct,
/// and list-of-enum — the latter two hand-built since <c>ArrowArrayBuilderFactory</c> doesn't
/// support struct/dictionary-typed elements; see <see cref="BuildListOfStructArray"/>/
/// <see cref="BuildListOfEnumArray"/>), <c>Dictionary&lt;K,V&gt;</c> (map, including an
/// enum-valued map — same hand-built rationale; see <see cref="BuildMapArrayWithEnumValues"/>),
/// enums (dictionary-encoded), nested dataclass-equivalents (embedded-IPC-in-binary — see
/// <see cref="BuildEmbeddedRecordArray"/>), and <see cref="RecordBatch"/> as a field value
/// (embedded IPC bytes directly, no property reflection — see
/// <see cref="BuildRecordBatchBinaryArray"/>). The wide/temporal/decimal-in-container and
/// dictionary-encoding-override Arrow types are still deferred — see docs/roadmap.md.</para>
/// </summary>
public static class ValueCodec
{
    /// <summary>Builds a 1-row <see cref="RecordBatch"/> from CLR values, in <paramref name="schema"/> field order.</summary>
    public static RecordBatch BuildRow(Schema schema, IReadOnlyList<object?> values)
    {
        var arrays = new IArrowArray[schema.FieldsList.Count];
        try
        {
            for (var i = 0; i < arrays.Length; i++)
            {
                arrays[i] = BuildSingleValueArray(schema.GetFieldByIndex(i), values[i]);
            }

            return new RecordBatch(schema, arrays, length: 1);
        }
        catch
        {
            foreach (var array in arrays)
            {
                array?.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Reconciles an inbound stream batch to a declared input schema: strict on the field set,
    /// tolerant of column order and compatible numeric widening (int32/int64/float32 → float64).
    /// Mirrors Python's <c>_coerce_input_batch</c>. Throws when the field set doesn't match or a
    /// same-named column's type isn't a coercion this port knows how to perform.
    /// </summary>
    public static RecordBatch CoerceBatch(RecordBatch batch, Schema targetSchema)
    {
        if (SchemasEqual(batch.Schema, targetSchema))
        {
            return batch;
        }

        var targetNames = targetSchema.FieldsList.Select(f => f.Name).ToList();
        var batchNames = batch.Schema.FieldsList.Select(f => f.Name).ToList();
        if (!new HashSet<string>(batchNames).SetEquals(targetNames))
        {
            throw new InvalidOperationException(
                $"Input schema mismatch (wrong column type/name set): expected [{string.Join(", ", targetNames)}], got [{string.Join(", ", batchNames)}].");
        }

        var arrays = new IArrowArray[targetSchema.FieldsList.Count];
        for (var i = 0; i < arrays.Length; i++)
        {
            var targetField = targetSchema.GetFieldByIndex(i);
            var sourceIndex = batchNames.IndexOf(targetField.Name);
            var sourceArray = batch.Column(sourceIndex);
            arrays[i] = CoerceArray(sourceArray, targetField.DataType);
        }

        return new RecordBatch(targetSchema, arrays, batch.Length);
    }

    /// <summary>Structural schema comparison (field count/name/type-id order) — <see cref="Schema"/>
    /// (Apache.Arrow) never overrides <see cref="object.Equals(object?)"/>, so it's reference
    /// equality by default and two independently-built schemas of identical shape compare unequal.
    /// Public so callers outside this class (e.g. <c>QueryFarm.VgiRpc.Http.ExternalLocation</c>'s
    /// resolved-pointer schema check) get the same loose, name/type-id-only comparison this class
    /// itself uses for its own fast-path check above, rather than each hand-rolling one.</summary>
    public static bool SchemasEqual(Schema a, Schema b)
    {
        if (a.FieldsList.Count != b.FieldsList.Count)
        {
            return false;
        }

        for (var i = 0; i < a.FieldsList.Count; i++)
        {
            if (a.GetFieldByIndex(i).Name != b.GetFieldByIndex(i).Name || a.GetFieldByIndex(i).DataType.TypeId != b.GetFieldByIndex(i).DataType.TypeId)
            {
                return false;
            }
        }

        return true;
    }

    private static IArrowArray CoerceArray(IArrowArray array, IArrowType targetType)
    {
        if (array.Data.DataType.TypeId == targetType.TypeId)
        {
            return array;
        }

        return (array, targetType) switch
        {
            (Int32Array a, DoubleType) => new DoubleArray.Builder().AppendRange(a.Values.ToArray().Select(v => (double)v)).Build(),
            (Int64Array a, DoubleType) => new DoubleArray.Builder().AppendRange(a.Values.ToArray().Select(v => (double)v)).Build(),
            (FloatArray a, DoubleType) => new DoubleArray.Builder().AppendRange(a.Values.ToArray().Select(v => (double)v)).Build(),
            _ => throw new InvalidOperationException(
                $"Input schema mismatch (wrong column type): cannot cast {array.Data.DataType} to {targetType}."),
        };
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
        if (clrTypes.Count == 0)
        {
            return [];
        }

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
            BinaryType when value is byte[] bytes => new BinaryArray.Builder().Append(bytes).Build(),
            BinaryType when value is RecordBatch recordBatchValue => BuildRecordBatchBinaryArray(recordBatchValue),
            BinaryType => BuildEmbeddedRecordArray(value),
            // LargeStringType/LargeBinaryType: only reachable via a [LargeWidth] parameter/return
            // (see LargeWidthAttribute) — used for the conformance suite's echo_large_string/
            // echo_large_binary, whose reference declares pa.large_string()/pa.large_binary().
            LargeStringType => new LargeStringArray.Builder().Append((string)value).Build(),
            LargeBinaryType when value is LargeBytesBuffer largeBuffer => BuildLargeBytesArray(largeBuffer),
            LargeBinaryType when value is byte[] largeBytes => new LargeBinaryArray.Builder().Append(largeBytes).Build(),
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
            Date32Type => new Date32Array.Builder().Append((DateOnly)value).Build(),
            // A naive DateTime carries no offset on the wire (tz=null) — re-tag as Kind=Utc
            // (relabels, does not convert) so the ticks-since-epoch calculation preserves the
            // literal wall-clock value instead of applying a timezone shift. See SchemaDerivation's
            // DateTime/DateTimeOffset split for why these are two distinct wire shapes.
            TimestampType timestampType when value is DateTime dt => new TimestampArray.Builder(timestampType).Append(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))).Build(),
            TimestampType timestampType => new TimestampArray.Builder(timestampType).Append((DateTimeOffset)value).Build(),
            Time64Type time64Type => new Time64Array.Builder(time64Type).Append((TimeOnly)value).Build(),
            DurationType durationType => new DurationArray.Builder(durationType).Append((TimeSpan)value).Build(),
            Decimal128Type decimal128Type => new Decimal128Array.Builder(decimal128Type).Append((decimal)value).Build(),
            ListType listType => BuildListArray(listType, (System.Collections.IEnumerable)value),
            StructType structType => BuildStructArray(structType, value, isEmpty: false),
            DictionaryType dictType => BuildEnumArray(dictType, value),
            MapType mapType => BuildMapArray(mapType, (System.Collections.IDictionary)value),
            var other => throw NotSupportedYet(other),
        };
    }

    private static IArrowArray BuildEmptyArrayOrNull(IArrowType type, bool nullRow) =>
        type switch
        {
            StringType => nullRow ? new StringArray.Builder().AppendNull().Build() : new StringArray.Builder().Build(),
            // Whether this BinaryType represents a byte[] or an embedded record is
            // indistinguishable (and irrelevant) for an empty/null array — both encode identically.
            BinaryType => nullRow ? new BinaryArray.Builder().AppendNull().Build() : new BinaryArray.Builder().Build(),
            LargeStringType => nullRow ? new LargeStringArray.Builder().AppendNull().Build() : new LargeStringArray.Builder().Build(),
            LargeBinaryType => nullRow ? new LargeBinaryArray.Builder().AppendNull().Build() : new LargeBinaryArray.Builder().Build(),
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
            Date32Type => nullRow ? new Date32Array.Builder().AppendNull().Build() : new Date32Array.Builder().Build(),
            TimestampType timestampType => nullRow ? new TimestampArray.Builder(timestampType).AppendNull().Build() : new TimestampArray.Builder(timestampType).Build(),
            Time64Type time64Type => nullRow ? new Time64Array.Builder(time64Type).AppendNull().Build() : new Time64Array.Builder(time64Type).Build(),
            DurationType durationType => nullRow ? new DurationArray.Builder(durationType).AppendNull().Build() : new DurationArray.Builder(durationType).Build(),
            Decimal128Type decimal128Type => nullRow ? new Decimal128Array.Builder(decimal128Type).AppendNull().Build() : new Decimal128Array.Builder(decimal128Type).Build(),
            // BuildListArray(empty-enumerable) means one row whose value is an empty list. A
            // zero-row record batch needs a genuinely zero-length list array instead; confusing
            // those two shapes produced a RecordBatch declared length=0 with a list column of
            // length=1, which Arrow .NET permits but every foreign Arrow reader correctly rejects.
            ListType listType => nullRow ? BuildListArray(listType, null) : BuildZeroRowListArray(listType),
            StructType structType => nullRow ? BuildStructArray(structType, value: null, isEmpty: false) : BuildStructArray(structType, value: null, isEmpty: true),
            DictionaryType dictType => nullRow
                ? new DictionaryArray(dictType, new Int16Array.Builder().AppendNull().Build(), new StringArray.Builder().Build())
                : new DictionaryArray(dictType, new Int16Array.Builder().Build(), new StringArray.Builder().Build()),
            MapType mapType => nullRow ? BuildMapArray(mapType, null) : BuildZeroRowMapArray(mapType),
            var other => throw NotSupportedYet(other),
        };

    private static IArrowArray BuildEmptyArray(IArrowType type) => BuildEmptyArrayOrNull(type, nullRow: false);

    private static IArrowArray BuildZeroRowListArray(ListType listType)
    {
        var values = BuildEmptyArray(listType.ValueDataType);
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Build();
        var data = new ArrayData(
            listType,
            length: 0,
            nullCount: 0,
            0,
            [Apache.Arrow.ArrowBuffer.Empty, offsets],
            [values.Data]);
        return new ListArray(data);
    }

    private static IArrowArray BuildZeroRowMapArray(MapType mapType)
    {
        var entryType = new StructType([mapType.KeyField, mapType.ValueField]);
        var entries = new StructArray(
            entryType,
            length: 0,
            [BuildEmptyArray(mapType.KeyField.DataType), BuildEmptyArray(mapType.ValueField.DataType)],
            Apache.Arrow.ArrowBuffer.Empty);
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Build();
        var data = new ArrayData(
            mapType,
            length: 0,
            nullCount: 0,
            0,
            [Apache.Arrow.ArrowBuffer.Empty, offsets],
            [entries.Data]);
        return new MapArray(data);
    }

    private static IArrowArray BuildListArray(ListType listType, System.Collections.IEnumerable? items)
    {
        // ArrowArrayBuilderFactory (which ListArray.Builder's constructor uses internally to
        // build its ValueBuilder) doesn't support struct-typed elements at all — list-of-struct
        // (needed for e.g. RichHeader.list_of_nested: List<Point>) has to be hand-built instead:
        // build each element as its own 1-row array (reusing BuildSingleValueArray, which
        // already knows how to build a struct), then concatenate them.
        if (listType.ValueDataType is StructType elementStructType)
        {
            return BuildListOfStructArray(listType, elementStructType, items);
        }

        // Same rationale as the StructType case above: ArrowArrayBuilderFactory (which
        // ListArray.Builder's constructor uses to build its ValueBuilder) doesn't support
        // dictionary-typed (enum) elements either — needed for e.g. NestedContainers.Statuses/
        // FrozenStatuses: List<Status>/HashSet<Status>.
        if (listType.ValueDataType is DictionaryType elementDictType)
        {
            return BuildListOfEnumArray(listType, elementDictType, items);
        }

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

    /// <summary>Builds a <c>list&lt;struct&gt;</c> array by building each element as its own
    /// 1-row struct array and concatenating them — see <see cref="BuildListArray"/>'s comment
    /// for why this bypasses the normal builder path.</summary>
    private static IArrowArray BuildListOfStructArray(ListType listType, StructType elementType, System.Collections.IEnumerable? items)
    {
        if (items is null)
        {
            var emptyValues = BuildStructArray(elementType, value: null, isEmpty: true);
            var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(0).Build();
            var validity = new ArrowBuffer.BitmapBuilder().Append(false).Build();
            var data = new ArrayData(listType, length: 1, nullCount: 1, 0, [validity, offsets], [emptyValues.Data]);
            return new ListArray(data);
        }

        var elements = items.Cast<object?>().ToList();
        var elementArrays = elements
            .Select(item => (IArrowArray)BuildStructArray(elementType, item, isEmpty: false))
            .ToList();
        var values = elementArrays.Count == 0
            ? BuildStructArray(elementType, value: null, isEmpty: true)
            : ArrowArrayConcatenator.Concatenate(elementArrays);

        var offsetsBuilder = new ArrowBuffer.Builder<int>();
        offsetsBuilder.Append(0);
        offsetsBuilder.Append(elements.Count);
        var listData = new ArrayData(listType, length: 1, nullCount: 0, 0, [Apache.Arrow.ArrowBuffer.Empty, offsetsBuilder.Build()], [values.Data]);
        return new ListArray(listData);
    }

    /// <summary>Builds a <c>list&lt;dictionary&lt;int16,utf8&gt;&gt;</c> array (list of enum) —
    /// NOT via the "build N one-row arrays and concatenate" pattern <see cref="BuildListOfStructArray"/>
    /// uses: <see cref="ArrowArrayConcatenator"/> doesn't carry a <see cref="DictionaryArray"/>'s
    /// dictionary values array (stored as <c>ArrayData.Dictionary</c>, a sidecar field distinct
    /// from its buffers/children — not something the concatenator's generic per-type logic
    /// populates), so concatenating dictionary-typed arrays silently drops it and the result fails
    /// to construct. <see cref="BuildEnumArrayMulti"/> builds the combined multi-row dictionary
    /// array directly instead.</summary>
    private static IArrowArray BuildListOfEnumArray(ListType listType, DictionaryType elementType, System.Collections.IEnumerable? items)
    {
        if (items is null)
        {
            var emptyValues = BuildEmptyArrayOrNull(elementType, nullRow: false);
            var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(0).Build();
            var validity = new ArrowBuffer.BitmapBuilder().Append(false).Build();
            var data = new ArrayData(listType, length: 1, nullCount: 1, 0, [validity, offsets], [emptyValues.Data]);
            return new ListArray(data);
        }

        var elements = items.Cast<object?>().ToList();
        var enumClrType = InferEnumClrTypeFromEnumerable(items, elements);
        var values = BuildEnumArrayMulti(elementType, enumClrType, elements);

        var offsetsBuilder = new ArrowBuffer.Builder<int>();
        offsetsBuilder.Append(0);
        offsetsBuilder.Append(elements.Count);
        var listData = new ArrayData(listType, length: 1, nullCount: 0, 0, [Apache.Arrow.ArrowBuffer.Empty, offsetsBuilder.Build()], [values.Data]);
        return new ListArray(listData);
    }

    /// <summary>Determines the concrete enum CLR type for a collection's elements — from the
    /// first non-null value where one exists, otherwise from the collection's own generic/array
    /// element type (so an empty enum list still builds a correctly-typed, if empty, dictionary
    /// array rather than failing outright). Mirrors <see cref="ExtractList"/>'s declared-type
    /// resolution, applied to a live object instead of a compile-time <see cref="Type"/>.</summary>
    private static Type InferEnumClrTypeFromEnumerable(System.Collections.IEnumerable items, IReadOnlyList<object?> elements)
    {
        foreach (var element in elements)
        {
            if (element is not null)
            {
                return element.GetType();
            }
        }

        var itemsType = items.GetType();
        return itemsType.IsArray
            ? itemsType.GetElementType()!
            : itemsType.GetGenericArguments() is [var t]
                ? t
                : throw new InvalidOperationException($"Cannot determine enum element type for '{itemsType}'.");
    }

    /// <summary>
    /// Builds a native Arrow <c>struct</c> array for a dataclass NESTED inside another
    /// dataclass's own fields (the "nested" tier of <see cref="SchemaDerivation"/>'s two-tier
    /// rule — the top-level tier uses <see cref="BuildEmbeddedRecordArray"/> instead).
    /// </summary>
    private static IArrowArray BuildStructArray(StructType structType, object? value, bool isEmpty)
    {
        var childArrays = new IArrowArray[structType.Fields.Count];
        for (var i = 0; i < childArrays.Length; i++)
        {
            var field = structType.Fields[i];
            if (isEmpty)
            {
                childArrays[i] = BuildEmptyArrayOrNull(field.DataType, nullRow: false);
            }
            else if (value is null)
            {
                childArrays[i] = BuildEmptyArrayOrNull(field.DataType, nullRow: true);
            }
            else
            {
                var property = value.GetType().GetProperty(FindClrPropertyName(value.GetType(), field));
                childArrays[i] = BuildSingleValueArray(field, property?.GetValue(value));
            }
        }

        var length = isEmpty ? 0 : 1;
        var nullCount = !isEmpty && value is null ? 1 : 0;
        var validityBuffer = nullCount > 0
            ? new Apache.Arrow.ArrowBuffer.BitmapBuilder().Append(false).Build()
            : Apache.Arrow.ArrowBuffer.Empty;
        var data = new ArrayData(structType, length, nullCount, 0, [validityBuffer], childArrays.Select(a => a.Data).ToArray());
        return new StructArray(data);
    }

    private static object ExtractStruct(StructArray array, int index, Type clrType)
    {
        var instance = Activator.CreateInstance(clrType)
            ?? throw new InvalidOperationException($"Cannot construct '{clrType}' — it needs a public parameterless constructor.");
        var structType = (StructType)array.Data.DataType;
        for (var i = 0; i < structType.Fields.Count; i++)
        {
            var field = structType.Fields[i];
            var property = clrType.GetProperty(FindClrPropertyName(clrType, field))!;
            property.SetValue(instance, ExtractSingleValue(array.Fields[i], index, property.PropertyType));
        }

        return instance;
    }

    /// <summary>
    /// Encodes a nested dataclass-equivalent as <c>binary</c> containing an embedded Arrow IPC
    /// stream: a schema message (from <see cref="SchemaDerivation.InnerSchemaFor"/>) followed by
    /// exactly one row and the EOS marker — matching the canonical Python implementation's
    /// encoding exactly (confirmed against the real reference client; a native Arrow
    /// <c>struct</c> column does NOT round-trip against it). Uses the stock, synchronous
    /// <see cref="ArrowStreamWriter"/> directly rather than <see cref="Wire.WireWriter"/>: this
    /// is a tiny in-memory serialization (never real I/O), doesn't need custom_metadata, and
    /// benefits from not needing to bridge to async for what's fundamentally synchronous work.
    /// </summary>
    private static IArrowArray BuildEmbeddedRecordArray(object value)
    {
        var clrType = value.GetType();
        var innerSchema = SchemaDerivation.InnerSchemaFor(clrType);
        var rowValues = new object?[innerSchema.FieldsList.Count];
        for (var i = 0; i < rowValues.Length; i++)
        {
            var field = innerSchema.GetFieldByIndex(i);
            var property = clrType.GetProperty(FindClrPropertyName(clrType, field))!;
            rowValues[i] = property.GetValue(value);
        }

        var row = BuildRow(innerSchema, rowValues);

        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, innerSchema, leaveOpen: true))
        {
            writer.WriteStart();
            writer.WriteRecordBatch(row);
            writer.WriteEnd();
        }

        return new BinaryArray.Builder().Append(stream.ToArray()).Build();
    }

    private static object ExtractEmbeddedRecord(byte[] bytes, Type clrType)
    {
        var innerSchema = SchemaDerivation.InnerSchemaFor(clrType);
        using var stream = new MemoryStream(bytes);
        using var reader = new ArrowStreamReader(stream);
        var row = reader.ReadNextRecordBatch()
            ?? throw new InvalidOperationException($"Embedded record for '{clrType}' had no data batch.");

        var instance = Activator.CreateInstance(clrType)
            ?? throw new InvalidOperationException($"Cannot construct '{clrType}' — it needs a public parameterless constructor.");
        for (var i = 0; i < innerSchema.FieldsList.Count; i++)
        {
            var field = innerSchema.GetFieldByIndex(i);
            var property = clrType.GetProperty(FindClrPropertyName(clrType, field))!;
            property.SetValue(instance, ExtractSingleValue(row.Column(i), 0, property.PropertyType));
        }

        return instance;
    }

    /// <summary>
    /// Encodes a <see cref="RecordBatch"/> field value directly as its own Arrow IPC stream
    /// bytes (schema + the batch's own rows + EOS) — unlike <see cref="BuildEmbeddedRecordArray"/>,
    /// there's no dataclass-property reflection involved: the RecordBatch's existing schema and
    /// data ARE what gets serialized. Matches Python's <c>pa.RecordBatch</c>-as-field rule
    /// (see <see cref="SchemaDerivation"/>'s doc comment) — always binary, at any nesting depth.
    /// </summary>
    private static IArrowArray BuildRecordBatchBinaryArray(RecordBatch batch)
    {
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, batch.Schema, leaveOpen: true))
        {
            writer.WriteStart();
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        return new BinaryArray.Builder().Append(stream.ToArray()).Build();
    }

    /// <summary>Inverse of <see cref="BuildRecordBatchBinaryArray"/>: reads the embedded IPC
    /// stream back into a <see cref="RecordBatch"/> as-is (own schema, own rows) — no property
    /// reflection, unlike <see cref="ExtractEmbeddedRecord"/>.</summary>
    private static RecordBatch ExtractRecordBatchFromBinary(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()
            ?? throw new InvalidOperationException("Embedded RecordBatch field had no data batch.");
    }

    /// <summary>
    /// Builds a single-row dictionary-encoded array for an enum value: the dictionary holds
    /// every member's wire name (in declaration order — a stable, deterministic ordering both
    /// sides can reproduce independently), and the one index selects <paramref name="value"/>'s
    /// member. Per WIRE_PROTOCOL.md §4: enum → dictionary(int16, utf8) by member name.
    /// </summary>
    private static IArrowArray BuildEnumArray(DictionaryType dictType, object value)
    {
        var enumType = value.GetType();
        var names = new List<string>();
        short selectedIndex = -1;
        short i = 0;
        foreach (var field in EnumFields(enumType))
        {
            names.Add(WireNaming.ForEnumMember(field));
            if (field.GetValue(null)!.Equals(value))
            {
                selectedIndex = i;
            }

            i++;
        }

        if (selectedIndex < 0)
        {
            throw new InvalidOperationException($"'{value}' is not a member of enum '{enumType}'.");
        }

        var dictionaryValues = new StringArray.Builder().AppendRange(names).Build();
        var indices = new Int16Array.Builder().Append(selectedIndex).Build();
        return new DictionaryArray(dictType, indices, dictionaryValues);
    }

    /// <summary>Multi-row twin of <see cref="BuildEnumArray"/>: one dictionary (every member's
    /// wire name, built once) shared by an N-row indices array — needed wherever a single
    /// combined <see cref="DictionaryArray"/> must hold more than one value (a list of enum, or
    /// an enum-valued map's values), since <see cref="ArrowArrayConcatenator"/> can't compose one
    /// from N one-row dictionary arrays (see <see cref="BuildListOfEnumArray"/>'s comment). A null
    /// element appends a null index (dictionary-encoded nulls are represented in the indices, not
    /// the dictionary itself).</summary>
    private static IArrowArray BuildEnumArrayMulti(DictionaryType dictType, Type enumType, IReadOnlyList<object?> values)
    {
        var names = new List<string>();
        var indexByMember = new Dictionary<object, short>();
        short i = 0;
        foreach (var field in EnumFields(enumType))
        {
            names.Add(WireNaming.ForEnumMember(field));
            indexByMember[field.GetValue(null)!] = i;
            i++;
        }

        var dictionaryValues = new StringArray.Builder().AppendRange(names).Build();
        var indexBuilder = new Int16Array.Builder();
        foreach (var value in values)
        {
            if (value is null)
            {
                indexBuilder.AppendNull();
                continue;
            }

            if (!indexByMember.TryGetValue(value, out var selectedIndex))
            {
                throw new InvalidOperationException($"'{value}' is not a member of enum '{enumType}'.");
            }

            indexBuilder.Append(selectedIndex);
        }

        return new DictionaryArray(dictType, indexBuilder.Build(), dictionaryValues);
    }

    private static object ExtractEnum(DictionaryArray array, int index, Type enumType)
    {
        var indices = (Int16Array)array.Indices;
        var dictionaryValues = (StringArray)array.Dictionary;
        var wireIndex = indices.Values[index];
        var name = dictionaryValues.GetString(wireIndex);
        foreach (var field in EnumFields(enumType))
        {
            if (WireNaming.ForEnumMember(field) == name)
            {
                return field.GetValue(null)!;
            }
        }

        throw new InvalidOperationException($"Enum '{enumType}' has no member matching wire name '{name}'.");
    }

    private static IEnumerable<FieldInfo> EnumFields(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static);

    private static IArrowArray BuildMapArray(MapType mapType, System.Collections.IDictionary? entries)
    {
        // MapArray.Builder's constructor calls ArrowArrayBuilderFactory.Build(mapType.ValueField.DataType)
        // for its ValueBuilder, which throws for a dictionary-typed (enum) value the same way it
        // does for struct — needed for e.g. NestedContainers.StatusByName: Dictionary<string, Status>.
        if (mapType.ValueField.DataType is DictionaryType)
        {
            return BuildMapArrayWithEnumValues(mapType, entries);
        }

        var builder = new MapArray.Builder(mapType);
        if (entries is null)
        {
            builder.AppendNull();
            return builder.Build();
        }

        builder.Append();
        foreach (System.Collections.DictionaryEntry entry in entries)
        {
            AppendScalarToBuilder(builder.KeyBuilder, mapType.KeyField.DataType, entry.Key);
            AppendScalarToBuilder(builder.ValueBuilder, mapType.ValueField.DataType, entry.Value);
        }

        return builder.Build();
    }

    /// <summary>Builds a <c>map(K, dictionary&lt;int16,utf8&gt;)</c> array (enum-valued map) by
    /// hand, bypassing <see cref="MapArray.Builder"/> entirely — its constructor eagerly builds
    /// BOTH key and value builders via the internal <c>ArrowArrayBuilderFactory</c>, which throws
    /// for a dictionary-typed value the same way it does for struct. Builds the key column as N
    /// one-row arrays concatenated (fine — keys are always a plain scalar type here, and
    /// concatenation only breaks for <see cref="DictionaryArray"/>, see
    /// <see cref="BuildListOfEnumArray"/>'s comment) and the value column as one combined
    /// dictionary array via <see cref="BuildEnumArrayMulti"/>, then composes them into a
    /// <c>list&lt;struct&lt;key, value&gt;&gt;</c> — a map's actual internal representation.
    /// </summary>
    private static IArrowArray BuildMapArrayWithEnumValues(MapType mapType, System.Collections.IDictionary? entries)
    {
        var entryStructType = new StructType([mapType.KeyField, mapType.ValueField]);
        var enumValueType = (DictionaryType)mapType.ValueField.DataType;

        if (entries is null)
        {
            var emptyEntries = BuildStructArray(entryStructType, value: null, isEmpty: true);
            var nullOffsets = new ArrowBuffer.Builder<int>().Append(0).Append(0).Build();
            var nullValidity = new ArrowBuffer.BitmapBuilder().Append(false).Build();
            var nullData = new ArrayData(mapType, length: 1, nullCount: 1, 0, [nullValidity, nullOffsets], [emptyEntries.Data]);
            return new MapArray(nullData);
        }

        // NOT entries.Cast<DictionaryEntry>(): Cast<T>'s own IEnumerable-typed parameter resolves
        // Dictionary<K,V>'s GENERIC enumerator (yielding boxed KeyValuePair<K,V>, not
        // DictionaryEntry) — only a foreach directly on an IDictionary-STATICALLY-typed reference
        // resolves IDictionary's own GetEnumerator() (yielding IDictionaryEnumerator/DictionaryEntry),
        // same as this method's non-enum sibling above already relies on.
        var pairs = new List<System.Collections.DictionaryEntry>();
        foreach (System.Collections.DictionaryEntry entry in entries)
        {
            pairs.Add(entry);
        }

        var keys = pairs.Select(p => p.Key).ToList();
        var values = pairs.Select(p => p.Value).ToList();

        var keyArray = pairs.Count == 0
            ? BuildEmptyArrayOrNull(mapType.KeyField.DataType, nullRow: false)
            : ArrowArrayConcatenator.Concatenate(keys.Select(key => BuildSingleValueArray(mapType.KeyField, key)).ToList());

        Type enumClrType;
        var firstNonNull = values.FirstOrDefault(v => v is not null);
        if (firstNonNull is not null)
        {
            enumClrType = firstNonNull.GetType();
        }
        else if (entries.GetType().GetGenericArguments() is [_, var valueClrType])
        {
            enumClrType = valueClrType;
        }
        else
        {
            throw new InvalidOperationException($"Cannot determine enum value type for '{entries.GetType()}'.");
        }

        var valueArray = BuildEnumArrayMulti(enumValueType, enumClrType, values);

        var entryData = new ArrayData(entryStructType, length: pairs.Count, nullCount: 0, 0, [Apache.Arrow.ArrowBuffer.Empty], [keyArray.Data, valueArray.Data]);
        var entriesArray = new StructArray(entryData);

        var offsetsBuilder = new ArrowBuffer.Builder<int>();
        offsetsBuilder.Append(0);
        offsetsBuilder.Append(pairs.Count);
        var mapData = new ArrayData(mapType, length: 1, nullCount: 0, 0, [Apache.Arrow.ArrowBuffer.Empty, offsetsBuilder.Build()], [entriesArray.Data]);
        return new MapArray(mapData);
    }

    private static object ExtractMap(MapArray array, int index, Type clrDictType)
    {
        var typeArgs = clrDictType.IsGenericType ? clrDictType.GetGenericArguments() : [];
        var keyType = typeArgs.Length > 0 ? typeArgs[0] : typeof(object);
        var valueType = typeArgs.Length > 1 ? typeArgs[1] : typeof(object);

        var start = array.ValueOffsets[index];
        var end = array.ValueOffsets[index + 1];
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;
        for (var i = start; i < end; i++)
        {
            var key = ExtractSingleValue(array.Keys, i, keyType);
            var val = ExtractSingleValue(array.Values, i, valueType);
            dict[key!] = val;
        }

        return dict;
    }

    /// <summary>Finds the CLR property on <paramref name="clrType"/> whose wire name (via
    /// <see cref="WireNaming.ForProperty"/>) matches <paramref name="wireField"/>'s name — the
    /// reverse of schema derivation, used to bind wire field values back to properties (also
    /// used by <see cref="Server.RpcServer"/> for stream headers, which follow the same
    /// property &lt;-&gt; wire-field binding as a struct/embedded-record's own fields).</summary>
    public static string FindClrPropertyName(Type clrType, Field wireField)
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
            case ListType innerListType:
                // Nested list (e.g. List<List<long>>) — ArrowArrayBuilderFactory already built
                // `builder` as a ListArray.Builder for this element field, so recurse into it.
                var innerListBuilder = (ListArray.Builder)builder;
                if (value is null)
                {
                    innerListBuilder.AppendNull();
                }
                else
                {
                    innerListBuilder.Append();
                    foreach (var innerItem in (System.Collections.IEnumerable)value)
                    {
                        AppendScalarToBuilder(innerListBuilder.ValueBuilder, innerListType.ValueDataType, innerItem);
                    }
                }

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
            // LargeStringArray: not part of this port's own schema derivation (no CLR type to
            // hang pa.large_string() off of yet — see docs/roadmap.md), but the conformance
            // suite's echo_large_string is driven by the canonical Python client, which builds
            // its request using the reference protocol's declared pa.large_string() column —
            // reading it here as a plain string is what lets that interop even though this
            // port's own outgoing schema for the same CLR `string` is always Utf8Type.
            LargeStringArray a => a.GetString(index),
            // LargeBinaryArray must be checked before the plain BinaryArray cases below (it's
            // not one — LargeStringArray/LargeBinaryArray are a distinct hierarchy — but is
            // listed here alongside its string sibling since both back a [LargeWidth] field).
            LargeBinaryArray a when effectiveType == typeof(LargeBytesBuffer) => LargeBytesBuffer.FromArray(a, index),
            LargeBinaryArray a when effectiveType == typeof(byte[]) => ExtractLargeBinaryValue(a, index),
            BinaryArray a when effectiveType == typeof(byte[]) => a.GetBytes(index).ToArray(),
            BinaryArray a when effectiveType == typeof(RecordBatch) => ExtractRecordBatchFromBinary(a.GetBytes(index).ToArray()),
            BinaryArray a => ExtractEmbeddedRecord(a.GetBytes(index).ToArray(), effectiveType),
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
            Date32Array a => a.GetDateOnly(index)!.Value,
            // GetTimestamp always returns a DateTimeOffset with the schema's declared offset
            // baked in (Offset=0 for both our naive and UTC-tagged shapes — see
            // SchemaDerivation) — .DateTime relabels that as Kind=Unspecified for the naive
            // (DateTime) case without changing the value; the UTC-tagged case keeps the
            // DateTimeOffset as-is.
            TimestampArray a when effectiveType == typeof(DateTime) => a.GetTimestamp(index)!.Value.DateTime,
            TimestampArray a => a.GetTimestamp(index)!.Value,
            Time64Array a => a.GetTime(index)!.Value,
            DurationArray a => a.GetTimeSpan(index)!.Value,
            Decimal128Array a => a.GetValue(index)!.Value,
            MapArray a => ExtractMap(a, index, effectiveType),
            ListArray a => ExtractList(a, index, effectiveType),
            StructArray a => ExtractStruct(a, index, effectiveType),
            DictionaryArray a => ExtractEnum(a, index, effectiveType),
            _ => throw NotSupportedYet(array.Data.DataType),
        };
    }

    private static LargeBinaryArray BuildLargeBytesArray(LargeBytesBuffer value)
    {
        var data = value.RetainBuffer();
        ArrowBuffer offsets = default;
        try
        {
            offsets = new ArrowBuffer.Builder<long>()
                .Append(0)
                .Append(data.Length)
                .Build();
            return new LargeBinaryArray(
                LargeBinaryType.Default,
                length: 1,
                offsets,
                data,
                ArrowBuffer.Empty);
        }
        catch
        {
            offsets.Dispose();
            data.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Materializes one <see cref="LargeBinaryArray"/> value as a <c>byte[]</c> — guarding
    /// explicitly against .NET's own single-array size ceiling (<see cref="System.Array.MaxLength"/>,
    /// ~2^31-57 bytes on every CLR, deliberately smaller than a large-binary column can represent)
    /// instead of letting <see cref="LargeBinaryArray.GetBytes(int)"/>'s internal
    /// <c>checked((int)...)</c> cast fail with a bare, unexplained <see cref="OverflowException"/>.
    ///
    /// A value this large cannot be held in a managed <c>byte[]</c> on any CLR — this is a
    /// genuine platform capability limit, not a configurable cap, so refusing it here (rather
    /// than crashing mid-extraction) is the correct behavior. The conformance suite's
    /// <c>large_payload.echo_binary_over_int32_max</c> test (2^31+1 bytes) explicitly sanctions
    /// exactly this: any typed error is an acceptable answer provided the connection survives
    /// (see the reference's <c>_accept_typed_refusal</c>). <see cref="QueryFarm.VgiRpc.Server.RpcServer"/>'s
    /// existing dispatch try/catch around <see cref="ExtractRow"/> already turns any exception thrown here
    /// into a normal wire-level error response — this method doesn't need its own error-kind or
    /// wire handling, only to fail with a clear message instead of an opaque overflow.
    /// </summary>
    private static byte[] ExtractLargeBinaryValue(LargeBinaryArray array, int index)
    {
        var offsets = array.ValueOffsets;
        var length = offsets[index + 1] - offsets[index];
        if (length > System.Array.MaxLength)
        {
            throw new NotSupportedException(
                $"Value is {length} bytes, which exceeds the largest byte[] this runtime can represent ({System.Array.MaxLength} bytes).");
        }

        return array.GetBytes(index).ToArray();
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

    private static NotSupportedException NotSupportedYet(IArrowType type) =>
        new($"Arrow type '{type}' is not yet supported by ValueCodec — deferred to Milestone 2's conformance work. See docs/roadmap.md.");
}
