using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Telekinesis.Cli;

/// <summary>
/// Pure projection and redaction for everything crossing from a live browser
/// page into the model (issue #51). CDP hands us the user's real session: URLs
/// carry tokens in the query, pages log secrets to the console, and RemoteObject
/// graphs carry object ids. Nothing reaches a tool result without passing
/// through here. Deterministic and browser-free, so it is unit-tested directly.
/// </summary>
internal static partial class CdpFormat
{
    /// <summary>Every emitted string is capped; longer values are cut and marked.</summary>
    internal const int Cap = 2048;

    internal const string Redacted = "[redacted]";

    /// <summary>
    /// Project a URL for output: keep scheme, host, port, path and query
    /// parameter NAMES; replace every query VALUE (access_token=…, sig=…) and
    /// drop the fragment entirely — an OAuth implicit flow puts the token
    /// after '#'. Userinfo (user:pass@) is dropped. data: URIs are summarized.
    /// </summary>
    internal static string ProjectUrl(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            var mime = comma > 5 ? raw[5..comma] : "";
            return Truncate($"data:{mime},[{Math.Max(raw.Length - comma - 1, 0)} bytes]");
        }
        // A protocol-relative URL ("//host/path?tok=…") parses as ABSOLUTE file://,
        // which percent-encodes the '?' into the path and skips the query loop below
        // — and invents a scheme the page never used. Treat it as text.
        if (raw.StartsWith("//", StringComparison.Ordinal))
            return Truncate(ScrubPatterns(raw));
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return Truncate(ScrubPatterns(raw)); // relative/odd scheme: scrub rather than parse

        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://").Append(uri.Host);
        if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
        sb.Append(uri.AbsolutePath);

        var query = uri.Query;
        if (query.Length > 1)
        {
            sb.Append('?');
            var first = true;
            foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!first) sb.Append('&');
                first = false;
                // A pair with no '=' is a VALUE, not a name — percent-encoding the
                // separator (?access_token%3D…) used to echo the whole token as if
                // it were a parameter name.
                var eq = pair.IndexOf('=');
                if (eq < 0) sb.Append(Redacted);
                else sb.Append(pair[..eq]).Append('=').Append(Redacted);
            }
        }
        // uri.Fragment is deliberately never appended.
        // The PATH gets the secret patterns too: dropping query values is not enough
        // when the token is a path segment (Slack/Discord webhooks, Telegram bot
        // tokens, signed one-time links, /verify/<jwt>). ScrubPatterns — not Scrub —
        // because Scrub's embedded-URL pre-pass would recurse straight back here.
        return Truncate(ScrubPatterns(sb.ToString()));
    }

    /// <summary>
    /// Replace known secret shapes in free text with <see cref="Redacted"/>.
    /// Best-effort by construction: a secret that looks like ordinary prose is
    /// not catchable, which is why tool output says so and bodies/headers are
    /// never emitted at all.
    /// </summary>
    internal static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // URLs EMBEDDED in free text get the same query-value projection as a url
        // field. Browsers routinely echo a full URL into console output — a CORS
        // or fetch failure quotes the request URL, tokens and all — so scrubbing
        // only standalone url fields would leak them through the message text.
        // (Only absolute http(s) URLs match, and ProjectUrl parses those without
        // calling back here, so there is no recursion.)
        text = EmbeddedUrl().Replace(text, m => ProjectUrl(m.Value));
        return ScrubPatterns(text);
    }

    /// <summary>The secret patterns alone — no embedded-URL pass, so
    /// <see cref="ProjectUrl"/> can call it without recursing.</summary>
    private static string ScrubPatterns(string text)
    {
        foreach (var pattern in Patterns)
            text = pattern.Replace(text, Redacted);
        return text;
    }

    /// <summary>Scrub and cap — the last call before any text leaves.</summary>
    internal static string Safe(string? text) => Truncate(Scrub(text));

    internal static string Truncate(string s) =>
        s.Length <= Cap ? s : s[..Cap] + "…";

    private static readonly Regex[] Patterns =
    [
        Jwt(), BearerOrBasic(), OpenAiKey(), GitHubToken(), SlackToken(),
        AwsKeyId(), GoogleKey(), StripeKey(), SlackWebhook(), Pem(),
        Assignment(), CamelAssignment(),
    ];

    /// <summary>camelCase secret keys — sessionToken, accessToken, apiKey. Kept
    /// case-SENSITIVE so the capital letter is the word boundary: "monkey=1" and
    /// "turnkey=2" must not match, while "apiKey=…" must.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])[a-z][A-Za-z0-9]*(?:Token|Secret|Password|Passwd|Key|Session|Sid|Auth)(?![a-z])\s*[:=]\s*(?!\[redacted\])\S+")]
    private static partial Regex CamelAssignment();

    /// <summary>Webhook/bot URLs whose secret is a PATH segment.</summary>
    [GeneratedRegex(@"(?i)(?:hooks\.slack\.com/services|discord(?:app)?\.com/api/webhooks)/[A-Za-z0-9_/-]{16,}|/bot\d{6,}:[A-Za-z0-9_-]{20,}")]
    private static partial Regex SlackWebhook();

    /// <summary>An absolute http(s) URL inside free text; stops at whitespace or
    /// the usual quoting/bracketing a message wraps it in.</summary>
    [GeneratedRegex("""(?i)https?://[^\s'"<>)\]}]+""")]
    private static partial Regex EmbeddedUrl();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/-]{16,}=*")]
    private static partial Regex BearerOrBasic();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyId();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\b[rs]k_(?:live|test)_[A-Za-z0-9]{16,}")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"-----BEGIN[^-]{0,40}(?:PRIVATE KEY|CERTIFICATE)-----[\s\S]*?-----END[^-]*-----")]
    private static partial Regex Pem();

    /// <summary>
    /// key=value / key: value where the key names a secret. The optional
    /// prefix group catches compound keys (access_token, x-api-key) that a
    /// plain \b would miss — '_' is a word character, so \btoken\b does NOT
    /// match inside "access_token". Session cookies are the likeliest secret
    /// shape in a logged-in browser, so session/sid/PHPSESSID are covered.
    /// The value lookahead skips pairs URL projection already redacted, so a
    /// projected query keeps its diagnostically useful parameter NAMES.
    /// </summary>
    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:[a-z0-9]+[_-])?(?:password|passwd|secret|token|apikey|api[_-]key|session|sessionid|jsessionid|phpsessid|sid|auth|authorization|pw|pass)(?![a-z0-9])\s*[:=]\s*(?!\[redacted\])\S+")]
    private static partial Regex Assignment();

    /// <summary>
    /// CDP console/log level → a small stable set. The protocol's type enum has
    /// eighteen values (clear, startGroup, profile, count, …), so this must never
    /// throw or drop: anything unknown is "info" and the raw type survives as the
    /// entry's source.
    /// </summary>
    internal static string Level(string? cdpType) => cdpType switch
    {
        "error" or "assert" => "error",
        "warning" => "warning",   // CDP says "warning", not "warn"
        "debug" or "verbose" => "debug",
        _ => "info",
    };

    /// <summary>
    /// Flatten a CDP RemoteObject to one short display string. Never copies an
    /// objectId (a handle into the live page), and never fails — an unknown
    /// shape degrades to its type name.
    /// </summary>
    internal static string Flatten(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return Safe(o.ToString());

        if (o.TryGetProperty("unserializableValue", out var un))
            return Safe(un.GetString());

        var type = Str(o, "type") ?? "object";
        var subtype = Str(o, "subtype");

        if (type is "string" or "number" or "boolean" or "symbol" or "bigint" or "undefined")
            return o.TryGetProperty("value", out var prim)
                ? Safe(prim.ValueKind == JsonValueKind.String ? prim.GetString() : prim.ToString())
                : Safe(Str(o, "description") ?? type);

        if (subtype == "null") return "null";
        if (type == "function") return Safe(Str(o, "description") ?? "function");

        // Objects/arrays: a by-value payload if the page gave one, else a depth-1
        // preview, else the description ("Array(3)", "HTMLDivElement").
        if (o.TryGetProperty("value", out var val) && val.ValueKind is not JsonValueKind.Undefined)
            return Safe(val.ToString());
        if (o.TryGetProperty("preview", out var preview))
            return Safe(RenderPreview(preview));
        return Safe(Str(o, "description") ?? Str(o, "className") ?? type);
    }

    private static string RenderPreview(JsonElement preview)
    {
        var description = Str(preview, "description");
        if (!preview.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
            return description ?? Str(preview, "type") ?? "object";

        var parts = new List<string>();
        foreach (var p in props.EnumerateArray())
        {
            var name = Str(p, "name");
            var value = Str(p, "value") ?? Str(p, "type") ?? "";
            parts.Add(string.IsNullOrEmpty(name) ? value : $"{name}: {value}");
            if (parts.Count >= 12) break;
        }
        var overflow = preview.TryGetProperty("overflow", out var of) && of.ValueKind == JsonValueKind.True ? ", …" : "";
        var body = "{" + string.Join(", ", parts) + overflow + "}";
        return string.IsNullOrEmpty(description) || description == "Object" ? body : $"{description} {body}";
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
