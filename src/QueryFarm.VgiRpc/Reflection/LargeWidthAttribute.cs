namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Marks a <c>string</c> parameter/return value or a <c>byte[]</c> parameter/return value as
/// needing Arrow's 64-bit-offset variant (<see cref="Apache.Arrow.Types.LargeStringType"/>/
/// <see cref="Apache.Arrow.Types.LargeBinaryType"/>) instead of the default 32-bit-offset
/// <see cref="Apache.Arrow.Types.StringType"/>/<see cref="Apache.Arrow.Types.BinaryType"/> — the
/// C# equivalent of Python's <c>Annotated[str, ArrowType(pa.large_string())]</c> /
/// <c>Annotated[bytes, ArrowType(pa.large_binary())]</c>.
///
/// Deliberately narrower than a general width-override attribute system (see docs/roadmap.md
/// M17): only the string/byte[] large variants are supported, since that's the only gap the
/// conformance suite's <c>large_payload</c> category exercises — every other width (int8/uint8/
/// decimal precision/etc.) already has a distinct CLR type to key off (see
/// <see cref="SchemaDerivation"/>'s type-mapping doc comment), so a general
/// <c>Annotated&lt;T, ArrowType&gt;</c>-style mechanism has no other caller yet. Only applies at
/// the top level (a service method's own parameter or return value) — matching every existing
/// Python usage of <c>pa.large_string()</c>/<c>pa.large_binary()</c>, which is likewise
/// top-level-only in the conformance protocol.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
public sealed class LargeWidthAttribute : Attribute;
