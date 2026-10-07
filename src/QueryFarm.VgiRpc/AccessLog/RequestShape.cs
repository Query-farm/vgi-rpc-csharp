using Apache.Arrow;
using QueryFarm.VgiRpc.Hash;

namespace QueryFarm.VgiRpc.AccessLog;

/// <summary>One request parameter as an access record describes it: its name and Arrow type,
/// never its value. Serialized as <c>{"name": ..., "type": ...}</c> in <c>request_fields</c>.</summary>
public sealed record AccessLogRequestField(string Name, string Type);

/// <summary>
/// The shape of a request batch -- what an access record says about a request instead of its
/// contents (<c>request_fields</c> / <c>request_rows</c>, access-log-spec.md §4.3).
/// </summary>
/// <remarks>
/// A request's values never reach a log, at any level, and there is no switch that brings them
/// back: the framework cannot know which parameters are secret, and a VGI <c>catalog_attach</c>
/// carries API keys and passwords as ordinary arguments. No digest either -- a hash of a request
/// whose other fields are known is a brute-force oracle for a short secret.
/// </remarks>
public static class RequestShape
{
    /// <summary>Describes <paramref name="batch"/> by field names, Arrow types and row count.</summary>
    public static (IReadOnlyList<AccessLogRequestField> Fields, long Rows) Of(RecordBatch batch)
    {
        var fields = new List<AccessLogRequestField>(batch.Schema.FieldsList.Count);
        foreach (var field in batch.Schema.FieldsList)
        {
            fields.Add(new AccessLogRequestField(field.Name, TypeName(field)));
        }

        return (fields, batch.Length);
    }

    // The canonical token where one exists; the Arrow library's own name otherwise. Logging must
    // never fail a call, and the type text is not compared across ports.
    private static string TypeName(Field field)
    {
        try
        {
            return TypeTokens.TypeToken(field);
        }
        catch (TypeTokens.UnsupportedArrowTypeException)
        {
            return field.DataType.Name;
        }
    }
}
