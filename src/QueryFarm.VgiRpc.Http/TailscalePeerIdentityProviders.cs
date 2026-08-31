using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QueryFarm.VgiRpc.Identity;

namespace QueryFarm.VgiRpc.Http;

/// <summary>Strict Tailscale Serve HTTP identity and capability evidence.</summary>
public static partial class TailscalePeerIdentityProviders
{
    private const string ProviderName = "tailscale";
    private const int MaxHeaderBytes = 65_536;
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    [GeneratedRegex("^=\\?utf-8\\?q\\?([^?]*)\\?=$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWordRegex();

    /// <summary>
    /// Trust Tailscale Serve headers only from exact immediate proxy peers. Funnel must not be
    /// used because it does not establish a Tailnet caller identity.
    /// </summary>
    public static IPeerIdentityProvider Serve(string issuer, IEnumerable<string> trustedProxyAddresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (ContainsControl(issuer)) throw new ArgumentException("issuer contains control characters", nameof(issuer));
        var proxies = TrustedProxyAddresses.Parse(trustedProxyAddresses, nameof(trustedProxyAddresses));
        return new ServeProvider(issuer, proxies);
    }

    private sealed class ServeProvider(string issuer, HashSet<string> proxies) : IPeerIdentityProvider
    {
        public string Provider => ProviderName;

        public ValueTask<PeerIdentityResult> ResolveAsync(PeerResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (TrustedProxyAddresses.Normalize(context.ImmediatePeer) is not { } peer
                || !proxies.Contains(peer))
                return ValueTask.FromResult(Result(PeerIdentityStatus.UntrustedProxy));
            try
            {
                var login = context.Header("Tailscale-User-Login");
                var displayName = context.Header("Tailscale-User-Name");
                var rawCapabilities = context.Header("Tailscale-App-Capabilities");
                var funnel = context.Header("Tailscale-Funnel-Request");
                if (funnel is not null)
                    return ValueTask.FromResult(Result(funnel == "?1"
                        ? PeerIdentityStatus.NotApplicable : PeerIdentityStatus.Invalid));
                if (login is null && rawCapabilities is null)
                    return ValueTask.FromResult(Result(displayName is null
                        ? PeerIdentityStatus.NoMatch : PeerIdentityStatus.Invalid));
                var capabilities = ParseCapabilities(
                    rawCapabilities is null ? null : DecodeHeader(rawCapabilities));
                if (login is null && capabilities.Count == 0)
                    return ValueTask.FromResult(Result(PeerIdentityStatus.NoMatch));
                var attributes = new Dictionary<string, object?>();
                var kind = PeerSubjectKind.Unknown;
                var stability = SubjectStability.None;
                string? subject = null;
                var verified = false;
                if (login is not null)
                {
                    login = DecodeHeader(login);
                    if (string.IsNullOrWhiteSpace(login)) return ValueTask.FromResult(Result(PeerIdentityStatus.Invalid));
                    kind = PeerSubjectKind.User;
                    stability = SubjectStability.Login;
                    subject = "login:" + login;
                    verified = true;
                    attributes["user_login"] = login;
                    if (displayName is not null) attributes["user_display_name"] = DecodeHeader(displayName);
                }
                else if (displayName is not null)
                    return ValueTask.FromResult(Result(PeerIdentityStatus.Invalid));

                var identity = new PeerIdentity(ProviderName, "serve_proxy", IdentityAssurance.ConfiguredProxy,
                    issuer, "http", kind, subject, stability, verified, attributes, capabilities,
                    rawCapabilities is not null,
                    context.AssertedPeer, context.ImmediatePeer);
                return ValueTask.FromResult(PeerIdentityResult.Available(identity));
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException
                or DecoderFallbackException or EncoderFallbackException or PeerIdentityRejectedException)
            {
                return ValueTask.FromResult(Result(PeerIdentityStatus.Invalid));
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseCapabilities(string? raw)
    {
        if (raw is null) return new Dictionary<string, object?>();
        if (s_strictUtf8.GetByteCount(raw) > MaxHeaderBytes || ContainsControl(raw))
            throw new ArgumentException("invalid capability header");
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("capabilities must be an object");
        ValidateJson(document.RootElement, 0, new Counter());
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("capability values must be arrays");
            capabilities.Add(property.Name, property.Value.Clone());
        }
        return capabilities;
    }

    private static void ValidateJson(JsonElement element, int depth, Counter counter)
    {
        if (depth > 16 || ++counter.Value > 4096) throw new ArgumentException("capability JSON exceeds limits");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                _ = s_strictUtf8.GetByteCount(property.Name);
                if (!names.Add(property.Name)) throw new ArgumentException("duplicate capability JSON key");
                ValidateJson(property.Value, depth + 1, counter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJson(item, depth + 1, counter);
        else if (element.ValueKind == JsonValueKind.String)
            _ = s_strictUtf8.GetByteCount(element.GetString()!);
        else if (element.ValueKind == JsonValueKind.Number && element.GetRawText().Length > 256)
            throw new ArgumentException("capability JSON number exceeds limits");
    }

    private static string DecodeHeader(string raw)
    {
        if (s_strictUtf8.GetByteCount(raw) > 4096 || ContainsControl(raw))
            throw new ArgumentException("invalid Tailscale identity header");
        var match = EncodedWordRegex().Match(raw);
        if (!match.Success) return raw;
        var encoded = match.Groups[1].Value.Replace('_', ' ');
        var bytes = new byte[encoded.Length];
        var length = 0;
        for (var index = 0; index < encoded.Length;)
        {
            var character = encoded[index];
            if (character == '=')
            {
                if (index + 2 >= encoded.Length) throw new ArgumentException("invalid RFC 2047 escape");
                var high = Hex(encoded[index + 1]);
                var low = Hex(encoded[index + 2]);
                if (high < 0 || low < 0) throw new ArgumentException("invalid RFC 2047 escape");
                bytes[length++] = (byte)((high << 4) | low);
                index += 3;
            }
            else
            {
                if (character > 0x7f) throw new ArgumentException("invalid RFC 2047 bytes");
                bytes[length++] = (byte)character;
                index++;
            }
        }
        var decoded = s_strictUtf8.GetString(bytes, 0, length);
        if (ContainsControl(decoded)) throw new ArgumentException("decoded identity contains controls");
        return decoded;
    }

    private static PeerIdentityResult Result(PeerIdentityStatus status) => new(ProviderName, status);
    private static bool ContainsControl(string value) => value.Any(character => character <= 0x1f || character == 0x7f);
    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
    private sealed class Counter { public int Value; }
}
