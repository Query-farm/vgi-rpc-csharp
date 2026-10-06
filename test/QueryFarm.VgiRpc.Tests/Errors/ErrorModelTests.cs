using System.Text;
using System.Text.Json;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Errors;

/// <summary>The gRPC-shaped error model (WIRE_PROTOCOL.md §8) -- the test vectors of
/// MULTI_PROTOCOL_HOSTING.md §3, emission and decode.</summary>
public class ErrorModelTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static Dictionary<string, string> Emit(Exception exc, bool traceback = true) =>
        LogMessage.FromException(exc, traceback).AddToMetadata();

    private static JsonElement Extra(Dictionary<string, string> metadata) => Json(metadata[MetadataKeys.LogExtra]);

    [Fact]
    public void TheSixteenCodesAndNothingElse()
    {
        Assert.Equal(16, ErrorCodes.All.Count);
        Assert.False(ErrorCodes.IsCanonical("OK"));
        Assert.Equal(ErrorCodes.Unknown, ErrorCodes.Parse("NOPE"));
        Assert.Equal(ErrorCodes.Unknown, ErrorCodes.Parse(null));
    }

    /// <summary>V1: code, kind and details at top level and mirrored in log_extra as an array.</summary>
    [Fact]
    public void AStatusExceptionCarriesAllThreeLayers()
    {
        var exc = new StatusException("down", ErrorCodes.Unavailable, "backend_down", [new RetryInfo(7)]);
        var md = Emit(exc);

        Assert.Equal("UNAVAILABLE", md[MetadataKeys.ErrorCode]);
        Assert.Equal("backend_down", md[MetadataKeys.ErrorKind]);
        Assert.Equal("""[{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":7}]""", md[MetadataKeys.ErrorDetails]);
        var extra = Extra(md);
        Assert.Equal("UNAVAILABLE", extra.GetProperty("error_code").GetString());
        Assert.Equal("backend_down", extra.GetProperty("error_kind").GetString());
        Assert.Equal(JsonValueKind.Array, extra.GetProperty("error_details").ValueKind);
        Assert.Equal(7, extra.GetProperty("error_details")[0].GetProperty("retry_delay_seconds").GetDouble());
    }

    /// <summary>The code is on every EXCEPTION batch -- an unclassified error is UNKNOWN.</summary>
    [Fact]
    public void AnUnclassifiedErrorIsUnknown()
    {
        var md = Emit(new InvalidOperationException("boom"));
        Assert.Equal("UNKNOWN", md[MetadataKeys.ErrorCode]);
        Assert.False(md.ContainsKey(MetadataKeys.ErrorKind));
        Assert.False(md.ContainsKey(MetadataKeys.ErrorDetails));
    }

    /// <summary>A code may be sent without a kind.</summary>
    [Fact]
    public void AnEmptyKindIsAbsent()
    {
        var md = Emit(new StatusException("x", ErrorCodes.Aborted, ""));
        Assert.Equal("ABORTED", md[MetadataKeys.ErrorCode]);
        Assert.False(md.ContainsKey(MetadataKeys.ErrorKind));
    }

    [Theory]
    [InlineData(4045, true)]
    [InlineData(4046, false)]
    public void TheCapBoundaryIsExactly4096Bytes(int padding, bool sent)
    {
        var details = new[] { new ErrorInfo(new Dictionary<string, string> { ["p"] = new string('x', padding) }).ToJson() };
        var encoded = ErrorModel.Encode(details);
        Assert.Equal(sent, encoded is not null);
        if (encoded is not null)
        {
            Assert.Equal(4096, Encoding.UTF8.GetByteCount(encoded));
        }
    }

    /// <summary>Measured in UTF-8 bytes, not characters.</summary>
    [Fact]
    public void TheCapIsMeasuredInBytes()
    {
        var details = new[] { new LocalizedMessage("fr", new string('é', 2048)).ToJson() };
        Assert.Null(ErrorModel.Encode(details));
    }

    /// <summary>Over the cap the whole array is dropped -- from both the top level and the mirror
    /// -- never trimmed to the elements that fit; the code and kind still go.</summary>
    [Fact]
    public void OversizedDetailsAreDroppedWhole()
    {
        var exc = new StatusException(
            "big", ErrorCodes.ResourceExhausted, "details_oversized",
            [new RetryInfo(1), new ErrorInfo(new Dictionary<string, string> { ["padding"] = new string('x', 5000) })]);
        var md = Emit(exc);

        Assert.False(md.ContainsKey(MetadataKeys.ErrorDetails));
        Assert.False(Extra(md).TryGetProperty("error_details", out _));
        Assert.Equal("RESOURCE_EXHAUSTED", md[MetadataKeys.ErrorCode]);
        Assert.Equal("details_oversized", md[MetadataKeys.ErrorKind]);

        var decoded = RpcErrorDecoder.Decode(new AnnotatedBatch(EmptyBatch(), md));
        Assert.Empty(decoded.ErrorDetails);
        Assert.False(decoded.IsRetryable());
    }

    /// <summary>V6: arrays breaking the catalog rules are dropped at emission.</summary>
    [Theory]
    [InlineData("""[{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":1},{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":2}]""")]
    [InlineData("""[{"@type":"vgi_rpc.Made.Up"}]""")]
    [InlineData("""[{"@type":"Unqualified"}]""")]
    [InlineData("""[{"no_type":1}]""")]
    public void RuleViolationsAreDroppedAtEmission(string array)
    {
        var details = ErrorModel.FromArray(Json(array));
        Assert.Null(ErrorModel.Encode(details));
        Assert.Throws<ArgumentException>(() => new StatusException("x", ErrorCodes.Internal, null, details));
    }

    [Fact]
    public void AStatusExceptionRefusesANonCanonicalCode() =>
        Assert.Throws<ArgumentException>(() => new StatusException("x", "OK"));

    /// <summary>The traceback setting removes the traceback and its company, and nothing else.</summary>
    [Fact]
    public void OmittingTheTracebackKeepsTheModel()
    {
        Exception exc;
        try
        {
            throw new StatusException("x", ErrorCodes.Internal, "k");
        }
        catch (Exception caught)
        {
            exc = caught;
        }

        var without = Extra(Emit(exc, traceback: false));
        foreach (var key in new[] { "traceback", "frames", "cause", "context" })
        {
            Assert.False(without.TryGetProperty(key, out _), key);
        }

        Assert.Equal("INTERNAL", without.GetProperty("error_code").GetString());
        Assert.True(Extra(Emit(exc, traceback: true)).TryGetProperty("traceback", out _));
    }

    /// <summary>V1/V2 through the decoder: verbatim code, kind and details, unknown types kept,
    /// typed access skipping them.</summary>
    [Fact]
    public void TheClientSurfacesAllThreeLayers()
    {
        var probe = Json("""{"@type":"conformance.Secondary.v1.Probe","note":"n"}""");
        var exc = new StatusException(
            "down", ErrorCodes.Unavailable, "backend_down",
            [new ErrorInfo(new Dictionary<string, string> { ["fixture"] = "f" }).ToJson(), new RetryInfo(7).ToJson(), probe]);
        var decoded = RpcErrorDecoder.Decode(new AnnotatedBatch(EmptyBatch(), Emit(exc)));

        Assert.Equal("UNAVAILABLE", decoded.ErrorCode);
        Assert.Equal("backend_down", decoded.ErrorKind);
        Assert.Equal(3, decoded.ErrorDetails.Count);
        Assert.Equal("conformance.Secondary.v1.Probe", decoded.ErrorDetails[2].GetProperty("@type").GetString());
        Assert.Equal(7, decoded.GetRetryInfo()!.RetryDelaySeconds);
        Assert.Equal("f", decoded.GetErrorInfo()!.Metadata["fixture"]);
        Assert.Equal([typeof(ErrorInfo), typeof(RetryInfo)], decoded.Details().Select(d => d.GetType()));
        Assert.True(decoded.IsRetryable());
    }

    /// <summary>A known kind still decodes to its subclass -- carrying what the server sent, not
    /// the subclass's own default.</summary>
    [Fact]
    public void ASubclassCarriesTheWireValues()
    {
        var md = new Dictionary<string, string>
        {
            [MetadataKeys.LogLevel] = "EXCEPTION",
            [MetadataKeys.LogMessage] = "draining",
            [MetadataKeys.ErrorKind] = MetadataKeys.ErrorKinds.ServerDraining,
        };
        var decoded = RpcErrorDecoder.Decode(new AnnotatedBatch(EmptyBatch(), md));
        Assert.IsType<ServerDrainingException>(decoded);
        Assert.Equal("", decoded.ErrorCode);
        Assert.Empty(decoded.ErrorDetails);
    }

    /// <summary>"" (a server older than the model) and "UNKNOWN" are different answers.</summary>
    [Fact]
    public void AnAbsentCodeIsEmptyNotUnknown()
    {
        var md = new Dictionary<string, string> { [MetadataKeys.LogLevel] = "EXCEPTION", [MetadataKeys.LogMessage] = "x" };
        var decoded = RpcErrorDecoder.Decode(new AnnotatedBatch(EmptyBatch(), md));
        Assert.Equal("", decoded.ErrorCode);
        Assert.Equal("UNKNOWN", decoded.CanonicalCode);
    }

    /// <summary>The log_extra mirror is the fallback when the top-level keys are absent.</summary>
    [Fact]
    public void TheMirrorIsTheFallback()
    {
        var md = new Dictionary<string, string>
        {
            [MetadataKeys.LogLevel] = "EXCEPTION",
            [MetadataKeys.LogMessage] = "x",
            [MetadataKeys.LogExtra] = """{"exception_type":"E","error_code":"ABORTED","error_kind":"k","error_details":[{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":3}]}""",
        };
        var decoded = RpcErrorDecoder.Decode(new AnnotatedBatch(EmptyBatch(), md));
        Assert.Equal("ABORTED", decoded.ErrorCode);
        Assert.Equal("k", decoded.ErrorKind);
        Assert.Equal(3, decoded.GetRetryInfo()!.RetryDelaySeconds);
    }

    /// <summary>V7: malformed details never turn the error into a decode failure.</summary>
    [Fact]
    public void ClientToleranceOfMalformedDetails()
    {
        Assert.Empty(ErrorModel.Decode("{\"not\":\"an array\"}"));
        Assert.Empty(ErrorModel.Decode("not json"));
        var kept = ErrorModel.Decode("""[1,{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":"soon"},{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":-1},{"@type":"vgi_rpc.ErrorInfo","metadata":{"a":1}}]""");
        Assert.Equal(3, kept.Count);
        Assert.Empty(ErrorModel.Typed(kept));
    }

    /// <summary>V3.</summary>
    [Theory]
    [InlineData("UNAVAILABLE", false, true)]
    [InlineData("RESOURCE_EXHAUSTED", true, true)]
    [InlineData("RESOURCE_EXHAUSTED", false, false)]
    [InlineData("ABORTED", true, false)]
    [InlineData("INTERNAL", true, false)]
    [InlineData("", false, false)]
    [InlineData("SOMETHING", false, false)]
    public void RetryabilityFollowsTheCode(string code, bool retryInfo, bool retryable)
    {
        JsonElement[] details = retryInfo ? [new RetryInfo(3).ToJson()] : [];
        Assert.Equal(retryable, ErrorModel.IsRetryable(code, details));
    }

    /// <summary>V8: every framework kind names its code.</summary>
    [Fact]
    public void EveryFrameworkKindCarriesItsCode()
    {
        Assert.Equal("UNIMPLEMENTED", ErrorModel.CodeOf(new MethodNotImplementedException("x")));
        Assert.Equal("UNIMPLEMENTED", ErrorModel.CodeOf(new ProtocolNotSupportedException("x")));
        Assert.Equal("INVALID_ARGUMENT", ErrorModel.CodeOf(new ProtocolNotSpecifiedException("x")));
        Assert.Equal("FAILED_PRECONDITION", ErrorModel.CodeOf(new ProtocolVersionException("x")));
        Assert.Equal("ABORTED", ErrorModel.CodeOf(new SessionLostException("x")));
        var draining = new ServerDrainingException("x");
        Assert.Equal("UNAVAILABLE", ErrorModel.CodeOf(draining));
        Assert.Equal(1, Assert.IsType<RetryInfo>(Assert.Single(draining.Details())).RetryDelaySeconds);
        Assert.Equal("UNAVAILABLE", ErrorModel.CodeOf(new QueryFarm.VgiRpc.Identity.IdentityUnavailableException("x", 9)));
        Assert.Equal("UNAUTHENTICATED", ErrorModel.CodeOf(new QueryFarm.VgiRpc.Identity.StaleAuthException("x")));
        Assert.Equal("PERMISSION_DENIED", ErrorModel.CodeOf(new QueryFarm.VgiRpc.Identity.IntrospectionRefusedException("x")));
        Assert.Equal("PERMISSION_DENIED", ErrorModel.CodeOf(new QueryFarm.VgiRpc.Identity.GrantRefusedException("x")));
        Assert.Equal("NOT_FOUND", ErrorModel.CodeOf(new QueryFarm.VgiRpc.Identity.TokenUnresolvedException("x")));
    }

    /// <summary>identity_unavailable carries RetryInfo with its own hint.</summary>
    [Fact]
    public void IdentityUnavailableCarriesItsHint()
    {
        var md = Emit(new QueryFarm.VgiRpc.Identity.IdentityUnavailableException("x", 9));
        Assert.Equal("""[{"@type":"vgi_rpc.RetryInfo","retry_delay_seconds":9}]""", md[MetadataKeys.ErrorDetails]);
    }

    [Fact]
    public void RetryInfoKeepsAFraction() =>
        Assert.Equal("2.5", new RetryInfo(2.5).ToJson().GetProperty("retry_delay_seconds").GetRawText());

    [Fact]
    public void APreconditionFailureNamesTheGatedProtocol()
    {
        var exc = new ProtocolVersionException("m", "ConformanceService", "1.0.0", "2.0.0");
        var violation = Assert.Single(exc.GetPreconditionFailure()!.Violations);
        Assert.Equal(("protocol_version", "ConformanceService"), (violation.Type, violation.Subject));
    }

    private static Apache.Arrow.RecordBatch EmptyBatch() =>
        new(new Apache.Arrow.Schema([], null), [], 0);
}
