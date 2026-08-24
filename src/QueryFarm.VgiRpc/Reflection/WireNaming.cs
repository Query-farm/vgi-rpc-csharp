using System.Reflection;
using System.Text;
using QueryFarm.VgiRpc.Attributes;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Derives snake_case wire names from idiomatic PascalCase/camelCase C# identifiers (an
/// <see cref="RpcNameAttribute"/> always wins when present). See docs/wire-protocol.md.
/// </summary>
public static class WireNaming
{
    /// <summary>Wire name for an RPC method: an explicit <see cref="RpcNameAttribute"/>, else the
    /// method name with a trailing "Async" stripped, converted to snake_case.</summary>
    public static string ForMethod(MethodInfo method)
    {
        if (method.GetCustomAttribute<RpcNameAttribute>() is { } attr)
        {
            return attr.WireName;
        }

        var name = method.Name;
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
        {
            name = name[..^"Async".Length];
        }

        return ToSnakeCase(name);
    }

    /// <summary>Wire name for a parameter: an explicit <see cref="RpcNameAttribute"/>, else its
    /// camelCase name converted to snake_case.</summary>
    public static string ForParameter(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<RpcNameAttribute>() is { } attr
            ? attr.WireName
            : ToSnakeCase(parameter.Name ?? throw new InvalidOperationException("Parameter has no name."));

    /// <summary>Wire name for a record/class property (nested dataclass-equivalent field).</summary>
    public static string ForProperty(PropertyInfo property) =>
        property.GetCustomAttribute<RpcNameAttribute>() is { } attr
            ? attr.WireName
            : ToSnakeCase(property.Name);

    /// <summary>
    /// Wire name for an enum member (dictionary-encoded by member name, per WIRE_PROTOCOL.md §4).
    /// Defaults to SCREAMING_SNAKE_CASE (e.g. C#'s <c>Pending</c> → <c>PENDING</c>) — Python enum
    /// members are used on the wire verbatim, and Python's own convention for them is
    /// upper-case, so this is the default most likely to match a hand-written Python enum
    /// without needing an <see cref="RpcNameAttribute"/> override on every member.
    /// </summary>
    public static string ForEnumMember(FieldInfo enumField) =>
        enumField.GetCustomAttribute<RpcNameAttribute>() is { } attr
            ? attr.WireName
            : ToSnakeCase(enumField.Name).ToUpperInvariant();

    /// <summary>
    /// Converts a PascalCase or camelCase identifier to snake_case: a lowercase letter/digit
    /// followed by an uppercase letter gets a break inserted, and a run of uppercase letters
    /// followed by a lowercase one breaks before the last uppercase letter of the run (so
    /// "EchoInt32" → "echo_int32" and "ID" inside "EchoID" → "echo_id", not "echo_i_d").
    /// </summary>
    public static string ToSnakeCase(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }

        var sb = new StringBuilder(identifier.Length + 8);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (char.IsUpper(c) && i > 0)
            {
                var prev = identifier[i - 1];
                var next = i + 1 < identifier.Length ? identifier[i + 1] : '\0';
                var boundary = char.IsLower(prev) || char.IsDigit(prev) ||
                    (char.IsUpper(prev) && char.IsLower(next));
                if (boundary)
                {
                    sb.Append('_');
                }
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
