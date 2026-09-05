using QueryFarm.VgiRpc.Client;

namespace QueryFarm.VgiRpc.Client.Http;

/// <summary>Routes <c>httpi://</c> requests through an Iroh provider and ordinary web URLs through a fallback handler.</summary>
public sealed class IrohHttpMessageHandler : HttpMessageHandler
{
    private const long MaxResponseHeaderBytes = 64L << 10;
    private readonly IrohEndpoint _endpoint;
    private readonly IrohConnectOptions _options;
    private readonly IIrohHttpTransportProvider _provider;
    private readonly HttpMessageInvoker? _fallback;

    public IrohHttpMessageHandler(
        IrohEndpoint endpoint,
        IrohConnectOptions? options = null,
        IIrohHttpTransportProvider? provider = null,
        HttpMessageHandler? fallback = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (_endpoint.Scheme != "httpi")
            throw new IrohUriException("IrohHttpMessageHandler requires an httpi:// endpoint.");
        _options = options ?? new IrohConnectOptions();
        _provider = provider ?? NativeIrohTransportProvider.Shared;
        _fallback = fallback is null ? null : new HttpMessageInvoker(fallback, disposeHandler: true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("HTTP-over-Iroh request has no URI.");
        if (!string.Equals(uri.Scheme, "httpi", StringComparison.Ordinal))
        {
            if (uri.Scheme is not ("http" or "https") || _fallback is null)
                throw new IrohUriException("Only httpi:// and configured HTTP fallback URLs are supported.");
            return await _fallback.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(uri.Host, _endpoint.EndpointId, StringComparison.Ordinal)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new IrohUriException("HTTP-over-Iroh request escaped its configured EndpointId.");

        var requestPath = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        if (!requestPath.StartsWith('/')) requestPath = "/" + requestPath;
        var path = _endpoint.BasePath + requestPath;
        var headers = new List<KeyValuePair<string, string>>();
        AddHeaders(headers, request.Headers);
        if (request.Content is not null) AddHeaders(headers, request.Content.Headers);
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var encodedLimit = Math.Max(HttpRpcClientOptions.DefaultAcceptedMaxResponseBytes,
            FindAcceptedResponseLimit(request));
        var native = await _provider.SendHttpAsync(_endpoint,
            new IrohHttpRequest(request.Method.Method, path, headers, body,
                encodedLimit, MaxResponseHeaderBytes), _options, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(native.RemoteEndpointId, _endpoint.EndpointId, StringComparison.Ordinal))
        {
            native.Body.Dispose();
            throw new IrohTransportException(
                "Iroh HTTP provider returned a response from the wrong EndpointId.",
                IrohErrorStage.Read, IrohErrorCategory.Authentication, IrohDispatchCertainty.Sent);
        }

        var response = new HttpResponseMessage((System.Net.HttpStatusCode)native.StatusCode)
        {
            RequestMessage = request,
            Content = new StreamContent(native.Body),
        };
        try
        {
            foreach (var (name, value) in native.Headers)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value)
                    && !response.Content.Headers.TryAddWithoutValidation(name, value))
                    throw new IrohTransportException($"Iroh HTTP response header '{name}' is invalid.",
                        IrohErrorStage.Read, IrohErrorCategory.Protocol, IrohDispatchCertainty.Sent);
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void AddHeaders(List<KeyValuePair<string, string>> target,
        System.Net.Http.Headers.HttpHeaders headers)
    {
        foreach (var header in headers)
            foreach (var value in header.Value)
                target.Add(new(header.Key, value));
    }

    private static long FindAcceptedResponseLimit(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues(HttpRpcClient.AcceptMaxResponseBytesHeader, out var values)
            && values.SingleOrDefault() is { } value
            && long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
            return parsed;
        return HttpRpcClientOptions.DefaultAcceptedMaxResponseBytes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fallback?.Dispose();
        base.Dispose(disposing);
    }
}
