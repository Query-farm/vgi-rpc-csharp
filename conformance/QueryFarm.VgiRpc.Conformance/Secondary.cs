using QueryFarm.VgiRpc.Attributes;
using QueryFarm.VgiRpc.Errors;

namespace QueryFarm.VgiRpc.Conformance;

/// <summary>
/// <c>conformance.Secondary.v1</c> -- the second application protocol every conformance worker
/// hosts beside <see cref="IConformanceService"/>, through the ordinary hosting API
/// (<see cref="Server.HostedProtocol"/>), on every transport. Normative in the reference's
/// <c>tools/cross-port/specs/MULTI_PROTOCOL_HOSTING.md</c> §2.
/// </summary>
/// <remarks>
/// <para><b>Routing by pair.</b> <c>echo_string</c> repeats the primary's name and signature; the
/// reply is prefixed, so a server keying dispatch on the bare method name answers with a wrong
/// value rather than a coincidentally right one.</para>
/// <para><b>A per-binding version gate.</b> Declares no protocol version while the primary
/// declares <c>2.0.0</c>, so a server that gates every call against the primary refuses these.</para>
/// <para>Pinned hash <c>58557cf1611546ad22d1c379bc3ce1b04166082f78375e9fc959f0086347eab6</c>;
/// the likeliest wrong digests come from <c>retry_delay_seconds</c> as float32 or a result entry
/// on a void method.</para>
/// </remarks>
[ProtocolName(Name)]
public interface ISecondary
{
    /// <summary>The fixture protocol's routing key.</summary>
    public const string Name = "conformance.Secondary.v1";

    /// <summary>Returns <see cref="SecondaryImpl.EchoPrefix"/> + <paramref name="value"/>.</summary>
    string EchoString(string value);

    /// <summary>Always raises: <paramref name="code"/>, <paramref name="kind"/> (absent when empty)
    /// and the fixed details.</summary>
    void Fail(string code, string kind, double retryDelaySeconds);

    /// <summary>Always raises, with details over the 4 KiB cap.</summary>
    void FailOversized();
}

/// <summary>The reference behaviour of <see cref="ISecondary"/>.</summary>
public sealed class SecondaryImpl : ISecondary
{
    /// <summary>What <see cref="EchoString"/> prepends.</summary>
    public const string EchoPrefix = "secondary:";

    /// <summary>A detail type no client knows, legitimately named under this protocol.</summary>
    public const string ProbeType = ISecondary.Name + ".Probe";

    /// <inheritdoc/>
    public string EchoString(string value) => EchoPrefix + value;

    /// <inheritdoc/>
    /// <remarks>A <paramref name="code"/> outside the closed set (the suite probes with <c>OK</c>)
    /// is itself refused with <c>INVALID_ARGUMENT</c> / <c>invalid_code</c> and a
    /// <see cref="BadRequest"/> naming the field.</remarks>
    public void Fail(string code, string kind, double retryDelaySeconds)
    {
        if (!ErrorCodes.IsCanonical(code))
        {
            throw new StatusException(
                $"'{code}' is not a canonical error code",
                ErrorCodes.InvalidArgument,
                "invalid_code",
                [new BadRequest([new FieldViolation("code", "must be a canonical code name")])]);
        }

        var details = new List<System.Text.Json.JsonElement>
        {
            new ErrorInfo(new Dictionary<string, string> { ["fixture"] = ISecondary.Name }).ToJson(),
        };
        if (retryDelaySeconds > 0)
        {
            details.Add(new RetryInfo(retryDelaySeconds).ToJson());
        }

        details.Add(System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["@type"] = ProbeType,
            ["note"] = "clients ignore detail types they do not know",
        }));
        throw new StatusException($"{ISecondary.Name} fail: {code} {kind}".TrimEnd(), code, kind, details);
    }

    /// <inheritdoc/>
    /// <remarks><see cref="RetryInfo"/> comes first and is small: a server dropping only the
    /// element that does not fit keeps it, and the error then reads as retryable -- the observable
    /// harm of partial delivery.</remarks>
    public void FailOversized() =>
        throw new StatusException(
            $"{ISecondary.Name} fail_oversized: details exceed 4 KiB",
            ErrorCodes.ResourceExhausted,
            "details_oversized",
            [new RetryInfo(1), new ErrorInfo(new Dictionary<string, string> { ["padding"] = new string('x', 5000) })]);
}
