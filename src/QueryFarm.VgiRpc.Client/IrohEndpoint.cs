namespace QueryFarm.VgiRpc.Client;

/// <summary>A canonical VGI endpoint carried by Iroh.</summary>
public sealed record IrohEndpoint
{
    public const string ArrowMuxAlpn = "vgi-rpc/arrow-mux/1";
    public const string HttpAlpn = "iroh-http/2";

    private IrohEndpoint(string scheme, string endpointId, byte[] endpointIdBytes, string basePath, string alpn)
    {
        Scheme = scheme;
        EndpointId = endpointId;
        EndpointIdBytes = endpointIdBytes;
        BasePath = basePath;
        Alpn = alpn;
    }

    public string Scheme { get; }
    public string EndpointId { get; }
    public byte[] EndpointIdBytes { get; }
    public string BasePath { get; }
    public string Alpn { get; }

    /// <summary>Parse without allowing <see cref="Uri"/> to normalize the authority or path.</summary>
    public static IrohEndpoint Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => c <= ' ' || c == '\u007f')
            || value.IndexOfAny(['\\', '?', '#']) >= 0)
        {
            throw new IrohUriException("Invalid VGI Iroh endpoint URI.");
        }

        const string irohPrefix = "iroh://";
        const string httpPrefix = "httpi://";
        string scheme;
        string remainder;
        if (value.StartsWith(irohPrefix, StringComparison.Ordinal))
        {
            scheme = "iroh";
            remainder = value[irohPrefix.Length..];
        }
        else if (value.StartsWith(httpPrefix, StringComparison.Ordinal))
        {
            scheme = "httpi";
            remainder = value[httpPrefix.Length..];
        }
        else
        {
            throw new IrohUriException("VGI Iroh endpoint scheme must be iroh:// or httpi://.");
        }

        var slash = remainder.IndexOf('/');
        var id = slash < 0 ? remainder : remainder[..slash];
        var path = slash < 0 ? string.Empty : remainder[slash..];
        if (id.Length != 64 || id.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
        {
            throw new IrohUriException("Iroh endpoint ID must be exactly 64 lowercase hexadecimal characters.");
        }
        if (scheme == "iroh" && path.Length != 0)
        {
            throw new IrohUriException("iroh:// endpoints cannot contain a path.");
        }
        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            throw new IrohUriException("httpi:// base paths cannot have a trailing empty segment.");
        if (path.Contains("//", StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new IrohUriException("httpi:// base paths cannot contain empty or dot segments.");
        }
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && (i + 2 >= path.Length || !Uri.IsHexDigit(path[i + 1]) || !Uri.IsHexDigit(path[i + 2])))
            {
                throw new IrohUriException("httpi:// base path contains an invalid percent escape.");
            }
            if (path[i] == '%')
            {
                var decoded = Convert.ToByte(path.Substring(i + 1, 2), 16);
                if (decoded is (byte)'.' or (byte)'/' or (byte)'\\' or <= 0x20 or 0x7f)
                    throw new IrohUriException("httpi:// base path contains an encoded dot, separator, or control.");
                i += 2;
            }
        }

        var basePath = path == "/" ? string.Empty : path;
        return new IrohEndpoint(scheme, id, Convert.FromHexString(id), basePath,
            scheme == "iroh" ? ArrowMuxAlpn : HttpAlpn);
    }
}
