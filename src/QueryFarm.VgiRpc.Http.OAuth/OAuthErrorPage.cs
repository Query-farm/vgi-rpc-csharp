using System.Net;

namespace QueryFarm.VgiRpc.Http.OAuth;

/// <summary>
/// Renders the OAuth flow's user-facing error page — a plain, functional equivalent of the
/// canonical Python repo's branded <c>_oauth_error_page</c> (which pulls in shared CSS/logo
/// constants this port doesn't have a home for yet). Deliberately not trying to pixel-match
/// Python's styling: this page has no wire contract and isn't exercised by conformance, so
/// "correct" here means legible and safe (HTML-escaped), not byte-identical markup.
/// </summary>
internal static class OAuthErrorPage
{
    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Authentication Error — vgi-rpc</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 32rem; margin: 4rem auto; padding: 0 1rem; color: #222; }
          h1 { font-size: 1.25rem; }
          .detail { color: #666; font-size: 0.9rem; white-space: pre-wrap; }
          a { color: #06c; }
        </style>
        </head>
        <body>
        <h1>Authentication Error</h1>
        <p>__MESSAGE__</p>
        __DETAIL__
        <p><a href="__RETRY_URL__">Try again</a></p>
        <footer>Powered by <a href="https://vgi-rpc.query.farm"><code>vgi-rpc</code></a></footer>
        </body>
        </html>
        """;

    public static string Render(string message, string? detail, string retryUrl)
    {
        // Plain token replacement, not string.Format: the template's own CSS rules contain
        // literal '{'/'}' braces that string.Format would otherwise try (and fail) to parse as
        // format placeholders.
        var detailHtml = string.IsNullOrEmpty(detail)
            ? string.Empty
            : $"<p class=\"detail\">{WebUtility.HtmlEncode(detail)}</p>";
        return Template
            .Replace("__MESSAGE__", WebUtility.HtmlEncode(message), StringComparison.Ordinal)
            .Replace("__DETAIL__", detailHtml, StringComparison.Ordinal)
            .Replace("__RETRY_URL__", WebUtility.HtmlEncode(retryUrl), StringComparison.Ordinal);
    }
}
