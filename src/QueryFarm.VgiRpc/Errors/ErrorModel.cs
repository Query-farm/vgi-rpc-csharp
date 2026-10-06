using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QueryFarm.VgiRpc.Errors;

/// <summary>
/// The closed set of canonical error codes: gRPC's sixteen, minus <c>OK</c> (WIRE_PROTOCOL.md §8).
/// </summary>
/// <remarks>
/// The wire value is the code's <em>name</em> -- <c>"UNAVAILABLE"</c>, never <c>14</c> -- so a
/// log line, a proxy rule and a client switch all read the same string. Strings rather than a
/// CLR enum because the value crosses the wire verbatim and a client must be able to hold (and
/// report) a value it does not recognise; <see cref="Parse"/> is what maps such a value to
/// <see cref="Unknown"/> for handling.
/// </remarks>
public static class ErrorCodes
{
    public const string Cancelled = "CANCELLED";
    public const string Unknown = "UNKNOWN";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
    public const string NotFound = "NOT_FOUND";
    public const string AlreadyExists = "ALREADY_EXISTS";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string ResourceExhausted = "RESOURCE_EXHAUSTED";
    public const string FailedPrecondition = "FAILED_PRECONDITION";
    public const string Aborted = "ABORTED";
    public const string OutOfRange = "OUT_OF_RANGE";
    public const string Unimplemented = "UNIMPLEMENTED";
    public const string Internal = "INTERNAL";
    public const string Unavailable = "UNAVAILABLE";
    public const string DataLoss = "DATA_LOSS";
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>All sixteen, in gRPC's numeric order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Cancelled, Unknown, InvalidArgument, DeadlineExceeded, NotFound, AlreadyExists,
        PermissionDenied, ResourceExhausted, FailedPrecondition, Aborted, OutOfRange,
        Unimplemented, Internal, Unavailable, DataLoss, Unauthenticated,
    ];

    private static readonly HashSet<string> s_all = new(All, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="value"/> is one of the sixteen names, exactly.</summary>
    public static bool IsCanonical(string? value) => value is not null && s_all.Contains(value);

    /// <summary>Reads a wire value for handling: anything unrecognised (or absent) is
    /// <see cref="Unknown"/>.</summary>
    public static string Parse(string? value) => IsCanonical(value) ? value! : Unknown;
}

/// <summary>One member of the fixed error-detail catalog (WIRE_PROTOCOL.md §8).</summary>
/// <remarks>
/// Each detail names its type in <c>@type</c>, mirroring protobuf's JSON form for <c>Any</c>.
/// Field names follow gRPC's, in snake_case. <c>DebugInfo</c> is deliberately absent: stack
/// traces are governed by the server's traceback setting instead.
/// </remarks>
public abstract record ErrorDetail
{
    /// <summary>The <c>@type</c> naming this detail on the wire.</summary>
    public abstract string TypeName { get; }

    /// <summary>The JSON object form, <c>@type</c> included.</summary>
    public JsonElement ToJson()
    {
        var obj = new JsonObject { ["@type"] = TypeName };
        WriteFields(obj);
        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>Writes this detail's own fields (everything but <c>@type</c>).</summary>
    protected abstract void WriteFields(JsonObject obj);

    /// <summary>Decodes one detail object, or <see langword="null"/> when it is not an object,
    /// names a type outside the catalog, or has a malformed field.</summary>
    /// <remarks>
    /// Clients ignore detail types they do not know, and a malformed known type is treated as
    /// absent rather than failing the error it rides on: the error is the news, the detail is
    /// commentary.
    /// </remarks>
    public static ErrorDetail? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("@type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return typeElement.GetString() switch
            {
                ErrorInfo.Type => ErrorInfo.FromJson(element),
                RetryInfo.Type => RetryInfo.FromJson(element),
                BadRequest.Type => BadRequest.FromJson(element),
                PreconditionFailure.Type => PreconditionFailure.FromJson(element),
                QuotaFailure.Type => QuotaFailure.FromJson(element),
                ResourceInfo.Type => ResourceInfo.FromJson(element),
                Help.Type => Help.FromJson(element),
                LocalizedMessage.Type => LocalizedMessage.FromJson(element),
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="typeName"/> is a catalog type.</summary>
    public static bool IsCatalogType(string typeName) => typeName is
        ErrorInfo.Type or RetryInfo.Type or BadRequest.Type or PreconditionFailure.Type
        or QuotaFailure.Type or ResourceInfo.Type or Help.Type or LocalizedMessage.Type;

    private protected static string Str(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"'{key}' must be a string");
    }

    private protected static IEnumerable<JsonElement> Objects(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"'{key}' must be an array of objects");
        }

        var items = value.EnumerateArray().ToList();
        return items.All(i => i.ValueKind == JsonValueKind.Object)
            ? items
            : throw new FormatException($"'{key}' must be an array of objects");
    }
}

/// <summary>Extra context for the reason. The reason and domain are already
/// <c>error_kind</c> and the protocol, so, unlike gRPC's, neither is repeated here.</summary>
/// <param name="Metadata">String-to-string context. Never credentials or user data.</param>
public sealed record ErrorInfo(IReadOnlyDictionary<string, string> Metadata) : ErrorDetail
{
    public const string Type = "vgi_rpc.ErrorInfo";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj)
    {
        var metadata = new JsonObject();
        foreach (var (key, value) in Metadata)
        {
            metadata[key] = value;
        }

        obj["metadata"] = metadata;
    }

    internal static ErrorInfo FromJson(JsonElement obj)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj.TryGetProperty("metadata", out var raw))
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("'metadata' must be an object of strings");
            }

            foreach (var property in raw.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("'metadata' must be an object of strings");
                }

                metadata[property.Name] = property.Value.GetString()!;
            }
        }

        return new ErrorInfo(metadata);
    }
}

/// <summary>How long to wait before retrying; a retry waits at least this long.</summary>
/// <param name="RetryDelaySeconds">Seconds; finite and non-negative.</param>
public sealed record RetryInfo(double RetryDelaySeconds) : ErrorDetail
{
    public const string Type = "vgi_rpc.RetryInfo";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj)
    {
        // A whole number travels as an integer, so the common case reads the same in every
        // language's JSON encoder ("7", not "7.0").
        obj["retry_delay_seconds"] = Math.Floor(RetryDelaySeconds) == RetryDelaySeconds
            && Math.Abs(RetryDelaySeconds) < 9_007_199_254_740_992d
            ? JsonValue.Create((long)RetryDelaySeconds)
            : JsonValue.Create(RetryDelaySeconds);
    }

    internal static RetryInfo FromJson(JsonElement obj)
    {
        if (!obj.TryGetProperty("retry_delay_seconds", out var raw) || raw.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("'retry_delay_seconds' must be a number");
        }

        var delay = raw.GetDouble();
        return double.IsFinite(delay) && delay >= 0
            ? new RetryInfo(delay)
            : throw new FormatException("'retry_delay_seconds' must be a finite, non-negative number");
    }
}

/// <summary>One wrong input.</summary>
public sealed record FieldViolation(string Field, string Description = "");

/// <summary>Which inputs were wrong.</summary>
public sealed record BadRequest(IReadOnlyList<FieldViolation> FieldViolations) : ErrorDetail
{
    public const string Type = "vgi_rpc.BadRequest";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj) =>
        obj["field_violations"] = new JsonArray(
            FieldViolations.Select(v => (JsonNode)new JsonObject { ["field"] = v.Field, ["description"] = v.Description }).ToArray());

    internal static BadRequest FromJson(JsonElement obj) =>
        new(Objects(obj, "field_violations").Select(v => new FieldViolation(Str(v, "field"), Str(v, "description"))).ToList());
}

/// <summary>One unmet precondition.</summary>
public sealed record PreconditionViolation(string Type, string Subject = "", string Description = "");

/// <summary>What state must change before the call can succeed.</summary>
public sealed record PreconditionFailure(IReadOnlyList<PreconditionViolation> Violations) : ErrorDetail
{
    public const string Type = "vgi_rpc.PreconditionFailure";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj) =>
        obj["violations"] = new JsonArray(
            Violations.Select(v => (JsonNode)new JsonObject
            {
                ["type"] = v.Type,
                ["subject"] = v.Subject,
                ["description"] = v.Description,
            }).ToArray());

    internal static PreconditionFailure FromJson(JsonElement obj) =>
        new(Objects(obj, "violations")
            .Select(v => new PreconditionViolation(Str(v, "type"), Str(v, "subject"), Str(v, "description"))).ToList());
}

/// <summary>One exhausted limit.</summary>
public sealed record QuotaViolation(string Subject, string Description = "");

/// <summary>Which limit was hit.</summary>
public sealed record QuotaFailure(IReadOnlyList<QuotaViolation> Violations) : ErrorDetail
{
    public const string Type = "vgi_rpc.QuotaFailure";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj) =>
        obj["violations"] = new JsonArray(
            Violations.Select(v => (JsonNode)new JsonObject { ["subject"] = v.Subject, ["description"] = v.Description }).ToArray());

    internal static QuotaFailure FromJson(JsonElement obj) =>
        new(Objects(obj, "violations").Select(v => new QuotaViolation(Str(v, "subject"), Str(v, "description"))).ToList());
}

/// <summary>Which object the error concerns.</summary>
public sealed record ResourceInfo(
    string ResourceType = "", string ResourceName = "", string Owner = "", string Description = "") : ErrorDetail
{
    public const string Type = "vgi_rpc.ResourceInfo";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj)
    {
        obj["resource_type"] = ResourceType;
        obj["resource_name"] = ResourceName;
        obj["owner"] = Owner;
        obj["description"] = Description;
    }

    internal static ResourceInfo FromJson(JsonElement obj) =>
        new(Str(obj, "resource_type"), Str(obj, "resource_name"), Str(obj, "owner"), Str(obj, "description"));
}

/// <summary>One pointer to documentation.</summary>
public sealed record HelpLink(string Description, string Url);

/// <summary>Where to read more.</summary>
public sealed record Help(IReadOnlyList<HelpLink> Links) : ErrorDetail
{
    public const string Type = "vgi_rpc.Help";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj) =>
        obj["links"] = new JsonArray(
            Links.Select(v => (JsonNode)new JsonObject { ["description"] = v.Description, ["url"] = v.Url }).ToArray());

    internal static Help FromJson(JsonElement obj) =>
        new(Objects(obj, "links").Select(v => new HelpLink(Str(v, "description"), Str(v, "url"))).ToList());
}

/// <summary>Text that is safe to show an end user. <c>error_message</c> stays developer-facing
/// English, as in gRPC; this is the one place user-facing text belongs.</summary>
public sealed record LocalizedMessage(string Locale, string Message) : ErrorDetail
{
    public const string Type = "vgi_rpc.LocalizedMessage";

    public override string TypeName => Type;

    protected override void WriteFields(JsonObject obj)
    {
        obj["locale"] = Locale;
        obj["message"] = Message;
    }

    internal static LocalizedMessage FromJson(JsonElement obj) => new(Str(obj, "locale"), Str(obj, "message"));
}

/// <summary>
/// The error model an exception carries onto the wire: a canonical code, an optional reason,
/// and typed details (WIRE_PROTOCOL.md §8).
/// </summary>
/// <remarks>
/// Implemented by <see cref="RpcException"/> and <see cref="AuthUnavailableException"/>. Any
/// exception that does not implement it is reported as <see cref="ErrorCodes.Unknown"/> with no
/// kind and no details.
/// </remarks>
public interface IRpcErrorModel
{
    /// <summary>The canonical code name, or <c>""</c> when the exception declares none.</summary>
    string ErrorCode { get; }

    /// <summary>The reason a client branches on, or <see langword="null"/>.</summary>
    string? ErrorKind { get; }

    /// <summary>The detail objects, in order.</summary>
    IReadOnlyList<JsonElement> ErrorDetails { get; }
}

/// <summary>Encoding, decoding and classification for the error model.</summary>
public static class ErrorModel
{
    /// <summary>Cap on the serialized <c>vgi_rpc.error_details</c> value, in UTF-8 bytes. A
    /// server whose array would exceed it omits the array entirely.</summary>
    public const int MaxErrorDetailsBytes = 4096;

    private const string ReservedPrefix = "vgi_rpc.";

    // Compact, UTF-8, not ASCII-escaped -- the cap is measured on exactly these bytes.
    private static readonly JsonSerializerOptions s_compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The canonical code <paramref name="exception"/> declares, or
    /// <see cref="ErrorCodes.Unknown"/>.</summary>
    public static string CodeOf(Exception exception) =>
        ErrorCodes.Parse(exception is IRpcErrorModel model ? model.ErrorCode : null);

    /// <summary>The non-empty kind <paramref name="exception"/> declares, or <see langword="null"/>.</summary>
    public static string? KindOf(Exception exception) =>
        exception is IRpcErrorModel { ErrorKind: { Length: > 0 } kind } ? kind : null;

    /// <summary>The detail objects <paramref name="exception"/> declares. Never throws: an
    /// exception whose details are broken still has to be reported.</summary>
    public static IReadOnlyList<JsonElement> DetailsOf(Exception exception)
    {
        try
        {
            return exception is IRpcErrorModel { ErrorDetails: { } details } ? details : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Converts typed details to their JSON objects.</summary>
    public static IReadOnlyList<JsonElement> ToJson(IEnumerable<ErrorDetail> details) =>
        details.Select(d => d.ToJson()).ToList();

    /// <summary>Applies the catalog rules: every element an object naming its type, no type
    /// twice, nothing in the reserved space outside the catalog, every type qualified.</summary>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    public static void Validate(IEnumerable<JsonElement> details)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in details)
        {
            if (detail.ValueKind != JsonValueKind.Object
                || !detail.TryGetProperty("@type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() is not { Length: > 0 } typeName)
            {
                throw new ArgumentException("every error detail must be an object naming its type in '@type'");
            }

            if (!seen.Add(typeName))
            {
                throw new ArgumentException($"error detail type '{typeName}' appears more than once");
            }

            if (typeName.StartsWith(ReservedPrefix, StringComparison.Ordinal) && !ErrorDetail.IsCatalogType(typeName))
            {
                throw new ArgumentException(
                    $"'{typeName}' claims the reserved 'vgi_rpc.' prefix but is not in the catalog. "
                    + "A protocol-defined detail type must live under its own protocol's name.");
            }

            if (!typeName.Contains('.', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{typeName}' is not qualified; protocol-defined types live under the protocol's name");
            }
        }
    }

    /// <summary>Serializes a detail list for <c>vgi_rpc.error_details</c>, or returns
    /// <see langword="null"/> -- meaning <em>omit the key</em> -- for an empty list, a list that
    /// breaks a catalog rule, and one whose serialized form exceeds
    /// <see cref="MaxErrorDetailsBytes"/>.</summary>
    /// <remarks>
    /// The array is dropped whole, never trimmed: a client cannot tell a truncated list from a
    /// complete one, so a partial list is worse than none.
    /// </remarks>
    public static string? Encode(IReadOnlyList<JsonElement> details)
    {
        if (details.Count == 0)
        {
            return null;
        }

        string text;
        try
        {
            Validate(details);
            text = JsonSerializer.Serialize(details, s_compact);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return Encoding.UTF8.GetByteCount(text) > MaxErrorDetailsBytes ? null : text;
    }

    /// <summary>Decodes a <c>vgi_rpc.error_details</c> value into its objects, in wire order.</summary>
    /// <remarks>
    /// Tolerant by design: anything that is not a JSON array decodes as empty and non-object
    /// elements are skipped. Unknown <c>@type</c> values are kept -- filtering to the catalog is
    /// what the typed accessors do.
    /// </remarks>
    public static IReadOnlyList<JsonElement> Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return FromArray(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The object elements of an already-parsed JSON array, cloned; empty for anything
    /// that is not an array.</summary>
    public static IReadOnlyList<JsonElement> FromArray(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList()
            : [];

    /// <summary>Whether WIRE_PROTOCOL.md §8 calls an error retryable.</summary>
    /// <remarks>
    /// <c>UNAVAILABLE</c> is retryable; <c>RESOURCE_EXHAUSTED</c> only when it carries
    /// <see cref="RetryInfo"/>. Everything else is final -- <c>ABORTED</c> included, which means
    /// "retry the whole operation at a higher level", not this call. A classification, not a
    /// policy: nothing in this library retries an RPC error automatically, because a method may
    /// not be idempotent.
    /// </remarks>
    public static bool IsRetryable(string? code, IEnumerable<JsonElement> details) =>
        ErrorCodes.Parse(code) switch
        {
            ErrorCodes.Unavailable => true,
            ErrorCodes.ResourceExhausted => details.Any(d => ErrorDetail.Parse(d) is RetryInfo),
            _ => false,
        };

    /// <summary>The catalog details this library understands, in wire order; unknown types and
    /// malformed entries skipped.</summary>
    public static IReadOnlyList<ErrorDetail> Typed(IEnumerable<JsonElement> details) =>
        details.Select(ErrorDetail.Parse).OfType<ErrorDetail>().ToList();
}
