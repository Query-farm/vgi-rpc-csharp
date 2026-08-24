namespace QueryFarm.VgiRpc.Logging;

/// <summary>
/// Severity levels for log messages emitted during RPC method processing, carried to the
/// client as zero-row batches interleaved with data batches. Mirrors Python's <c>log.Level</c>
/// exactly (member names ARE the wire values — see <see cref="Wire.MetadataKeys"/>). Named
/// <c>VgiLogLevel</c>, not <c>LogLevel</c>, to avoid colliding with
/// <see cref="Microsoft.Extensions.Logging.LogLevel"/> when both are in scope.
/// </summary>
public enum VgiLogLevel
{
    Exception,
    Error,
    Warn,
    Info,
    Debug,
    Trace,
}

internal static class VgiLogLevelExtensions
{
    /// <summary>The exact wire string for this level (Python's <c>Level.value</c>) — upper-case member name.</summary>
    public static string ToWireString(this VgiLogLevel level) => level.ToString().ToUpperInvariant();
}
