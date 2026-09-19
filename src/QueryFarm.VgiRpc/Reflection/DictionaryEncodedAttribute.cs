namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Declares that a <c>string</c> -- or each <c>string</c> element of a collection -- is carried as
/// Arrow's <c>dictionary&lt;int16, utf8&gt;</c> rather than the plain <c>utf8</c> a CLR string
/// would otherwise infer. The C# counterpart of Python's
/// <c>Annotated[str, ArrowType(pa.dictionary(pa.int16(), pa.string()))]</c>.
/// </summary>
/// <remarks>
/// <para>
/// An enum already maps to the same wire type, which is why a protocol declaring a
/// dictionary-encoded string could be mirrored with an enum and still hash identically. But an
/// enum's dictionary is a closed set of member names and a dictionary-encoded string is any
/// string: the stand-in refused every value that was not a member name. This declares the type
/// the reference means.
/// </para>
/// <para>
/// Narrow on purpose, like <see cref="LargeWidthAttribute"/> and <see cref="FixedBinaryAttribute"/>:
/// one Arrow type with a real caller, not a general type-override mechanism.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Property)]
public sealed class DictionaryEncodedAttribute : Attribute;
