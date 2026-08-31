using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QueryFarm.VgiRpc.Identity;

namespace QueryFarm.VgiRpc.Http;

/// <summary>No-cache Tailscale LocalAPI WhoIs peer evidence.</summary>
public sealed class TailscaleLocalApiProvider : IPeerIdentityProvider
{
    private const int MaxResponseBytes = 65_536;
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private readonly string _issuer;
    private readonly ITailscaleLocalApiClient _client;

    public TailscaleLocalApiProvider(string issuer, ITailscaleLocalApiClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        _ = s_strictUtf8.GetByteCount(issuer);
        if (issuer.Any(character => character <= 0x1f || character == 0x7f))
            throw new ArgumentException("issuer contains control characters", nameof(issuer));
        _issuer = issuer;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Provider => "tailscale";

    public async ValueTask<PeerIdentityResult> ResolveAsync(PeerResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.ImmediatePeer is null && context.SourceEndpoint is null && context.AssertedPeer is null)
            return Result(PeerIdentityStatus.NotApplicable);
        try
        {
            var response = await _client.WhoIsAsync(context, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == 404) return Result(PeerIdentityStatus.NoMatch);
            if (response.StatusCode is 401 or 403) return Result(PeerIdentityStatus.PermissionDenied);
            if (response.StatusCode is >= 500 and <= 599) return Result(PeerIdentityStatus.Unavailable);
            if (response.StatusCode != 200) return Result(PeerIdentityStatus.Invalid);
            if (response.ContentTypes is not { Count: 1 }
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    response.ContentTypes[0].Split(';', 2)[0].Trim(), "application/json"))
                return Result(PeerIdentityStatus.Invalid);
            if (response.Body.Length > MaxResponseBytes) return Result(PeerIdentityStatus.Invalid);
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateJson(document.RootElement, 0, new Counter());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result(PeerIdentityStatus.Invalid);
            var node = Property(root, "Node");
            var profile = Property(root, "UserProfile");
            var tags = Strings(Property(node, "Tags"));
            string subject;
            PeerSubjectKind kind;
            if (tags.Count > 0)
            {
                var stableId = Text(node, "StableID");
                if (stableId is null) return Result(PeerIdentityStatus.Invalid);
                subject = "node:" + stableId;
                kind = PeerSubjectKind.TaggedNode;
            }
            else
            {
                var userId = StableUserId(Property(profile, "ID"));
                if (userId is null) return Result(PeerIdentityStatus.Invalid);
                subject = "user:" + userId;
                kind = PeerSubjectKind.User;
            }
            var attributes = new Dictionary<string, object?>
            {
                ["tags"] = tags,
                ["capability_target"] = context.ServiceName is not null
                    ? new Dictionary<string, object?> { ["kind"] = "service", ["value"] = context.ServiceName }
                    : context.DestinationAddress is not null
                        ? new Dictionary<string, object?>
                        {
                            ["kind"] = "destination_ip",
                            ["value"] = NormalizeDestinationIp(context.DestinationAddress),
                        }
                        : new Dictionary<string, object?> { ["kind"] = "node" },
            };
            Put(attributes, "user_id", StableUserId(Property(profile, "ID")));
            Put(attributes, "user_login", Text(profile, "LoginName"));
            Put(attributes, "user_display_name", Text(profile, "DisplayName"));
            Put(attributes, "node_id", Text(node, "StableID"));
            Put(attributes, "node_name", Text(node, "Name"));
            var capabilities = ObjectOfArrays(Property(root, "CapMap"));
            var identity = new PeerIdentity(Provider, "localapi", IdentityAssurance.LocalDaemon,
                _issuer, context.Transport, kind, subject, SubjectStability.Stable, true,
                attributes, capabilities, true,
                NormalizeDestinationIp(context.AssertedPeer ?? context.SourceEndpoint ?? context.ImmediatePeer!));
            return PeerIdentityResult.Available(identity);
        }
        catch (TailscaleLocalApiPermissionException) { return Result(PeerIdentityStatus.PermissionDenied); }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or OperationCanceledException)
        { return Result(PeerIdentityStatus.Unavailable); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { return Result(PeerIdentityStatus.Invalid); }
    }

    private static PeerIdentityResult Result(PeerIdentityStatus status) => new("tailscale", status);
    private static string NormalizeDestinationIp(string value)
    {
        if (IPAddress.TryParse(value, out var direct)) return direct.ToString();
        if (IPEndPoint.TryParse(value, out var endpoint)) return endpoint.Address.ToString();
        throw new ArgumentException("destination address must contain an IP address");
    }
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    private static string? Text(JsonElement value, string name)
    {
        var item = Property(value, name);
        return item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text ? text : null;
    }
    private static string? StableUserId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    private static IReadOnlyList<string> Strings(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("tags must be an array");
        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException("tags must be strings");
            var tag = item.GetString()!;
            _ = s_strictUtf8.GetByteCount(tag);
            if (!tag.StartsWith("tag:", StringComparison.Ordinal) || tag.Length == 4
                || tag.Any(character => character <= 0x1f || character == 0x7f))
                throw new ArgumentException("invalid Tailscale tag");
            return tag;
        }).ToArray();
    }
    private static IReadOnlyDictionary<string, object?> ObjectOfArrays(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new Dictionary<string, object?>();
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("CapMap must be an object");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("CapMap values must be arrays");
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }
    private static void Put(Dictionary<string, object?> target, string name, string? value)
    { if (value is not null) target[name] = value; }
    private static void ValidateJson(JsonElement element, int depth, Counter counter)
    {
        if (depth > 16 || ++counter.Value > 4096) throw new ArgumentException("LocalAPI JSON exceeds limits");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                _ = s_strictUtf8.GetByteCount(property.Name);
                if (!names.Add(property.Name)) throw new ArgumentException("duplicate LocalAPI JSON key");
                ValidateJson(property.Value, depth + 1, counter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJson(item, depth + 1, counter);
        else if (element.ValueKind == JsonValueKind.String)
            _ = s_strictUtf8.GetByteCount(element.GetString()!);
        else if (element.ValueKind == JsonValueKind.Number && element.GetRawText().Length > 256)
            throw new ArgumentException("LocalAPI JSON number exceeds limits");
    }
    private sealed class Counter { public int Value; }
}

public interface ITailscaleLocalApiClient
{
    ValueTask<TailscaleLocalApiResponse> WhoIsAsync(PeerResolutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record TailscaleLocalApiResponse(
    int StatusCode, byte[] Body, IReadOnlyList<string>? ContentTypes = null);
public sealed class TailscaleLocalApiPermissionException(string message) : IOException(message);

/// <summary>Supported LocalAPI transports. Every WhoIs call performs a request; no result cache exists.</summary>
public sealed class TailscaleLocalApiHttpClient : ITailscaleLocalApiClient, IDisposable
{
    private const int MaxResponseBytes = 65_536;
    private readonly System.Net.Http.HttpClient _http;
    private readonly string? _token;

    public TailscaleLocalApiHttpClient(Uri endpoint, string? token = null)
        : this(endpoint, token, HandlerForHttp()) { }

    private TailscaleLocalApiHttpClient(Uri endpoint, string? token, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("LocalAPI endpoint must be an absolute HTTP URI without credentials, query, or fragment",
                nameof(endpoint));
        if (token?.Any(character => character <= 0x1f || character == 0x7f) == true)
            throw new ArgumentException("LocalAPI token contains control characters", nameof(token));
        _http = new System.Net.Http.HttpClient(handler) { BaseAddress = endpoint };
        _token = token;
    }

    public static TailscaleLocalApiHttpClient ForUnixSocket(string socketPath, string? token = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        return new TailscaleLocalApiHttpClient(new Uri("http://local-tailscaled.sock"), token, handler);
    }

    public static TailscaleLocalApiHttpClient ForWindowsNamedPipe(
        string pipeName, string serverName = ".", string? token = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                try { await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false); return pipe; }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            },
        };
        return new TailscaleLocalApiHttpClient(new Uri("http://local-tailscaled.sock"), token, handler);
    }

    public async ValueTask<TailscaleLocalApiResponse> WhoIsAsync(PeerResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        var source = context.AssertedPeer ?? context.SourceEndpoint ?? context.ImmediatePeer
            ?? throw new ArgumentException("WhoIs requires a peer address");
        var query = "addr=" + Uri.EscapeDataString(source) + "&proto=tcp";
        if (context.ServiceName is not null) query += "&svc_name=" + Uri.EscapeDataString(context.ServiceName);
        else if (context.DestinationAddress is not null)
            query += "&dst_ip=" + Uri.EscapeDataString(DestinationIp(context.DestinationAddress));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/localapi/v0/whois?" + query);
        request.Headers.Host = "local-tailscaled.sock";
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(_token))
        {
            var proof = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _token));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", proof);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var contentTypes = response.Content.Headers.TryGetValues("Content-Type", out var values)
            ? values.ToArray() : [];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = new byte[MaxResponseBytes + 1];
        var length = 0;
        while (length < body.Length)
        {
            var count = await stream.ReadAsync(body.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length != body.Length) Array.Resize(ref body, length);
        return new TailscaleLocalApiResponse((int)response.StatusCode, body, contentTypes);
    }

    public void Dispose() => _http.Dispose();
    private static HttpMessageHandler HandlerForHttp() => new SocketsHttpHandler { UseProxy = false };

    private static string DestinationIp(string value)
    {
        if (IPAddress.TryParse(value, out var direct)) return direct.ToString();
        if (IPEndPoint.TryParse(value, out var endpoint)) return endpoint.Address.ToString();
        throw new ArgumentException("destination address must contain an IP address");
    }
}
