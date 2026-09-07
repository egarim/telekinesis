using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Telekinesis.Cli;

/// <summary>
/// The CDP action tier (issue #51). JavaScript in a logged-in page runs with the
/// user's whole session — cookies, storage, authenticated fetch — so evaluation
/// is an ACTION, not a read: it is registered next to <see cref="ActionTools"/>
/// (never via the provider's perception ToolTypes), absent in --read-only mode,
/// and audit-logged.
/// </summary>
[McpServerToolType]
public static class BrowserEvalTools
{
    [McpServerTool(Name = "browser_evaluate")]
    [Description("Run a JavaScript expression in a browser page. This is an ACTION, not a read: it executes with the user's full session for every site they are logged into. Objects come back as a shallow preview — wrap in JSON.stringify(...) for full data. The expression is audit-logged (the result is not). Never use it to read a credential; that is what fill_credential is for.")]
    public static async Task<string> BrowserEvaluate(
        CdpSessionService cdp,
        [Description("JavaScript expression to evaluate in the page.")] string expression,
        [Description("Target id from browser_targets; empty uses the only open page.")] string? targetId,
        CancellationToken ct)
    {
        var session = await cdp.AttachAsync(targetId, ct);
        var ok = false;
        try
        {
            var result = await session.CallAsync("Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                silent = true,
                generatePreview = true,
                timeout = 5000,
                // returnByValue is deliberately NOT set: it hard-fails on DOM nodes
                // and circular objects. A preview always renders.
            }, ct);

            // Two independent failure channels: a transport error surfaces as an
            // exception from CallAsync, but a THROWN expression is a successful
            // reply carrying exceptionDetails.
            if (result.TryGetProperty("exceptionDetails", out var details))
            {
                var text = details.TryGetProperty("exception", out var ex)
                    ? CdpFormat.Flatten(ex)
                    : CdpFormat.Safe(details.TryGetProperty("text", out var t) ? t.GetString() : "threw");
                return Audit(session, expression, false, new { status = "threw", text });
            }

            ok = true;
            var value = result.TryGetProperty("result", out var r) ? CdpFormat.Flatten(r) : "";
            return Audit(session, expression, true, new { status = "ok", value });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Audit(session, expression, ok, new { status = "error", error = CdpFormat.Safe(ex.Message) });
        }
    }

    private static string Audit(CdpSession session, string expression, bool success, object payload)
    {
        // The expression is recorded; the RESULT never is — it may carry page data.
        // The expression itself is SCRUBBED first: an agent can embed a token in it
        // (fetch with an Authorization header), and AuditLog's contract is that
        // secrets never reach the log file.
        Console.Error.WriteLine(
            $"[telekinesis] {DateTimeOffset.Now:O} browser_evaluate target={session.Url} success={success}");
        AuditLog.Append("browser_evaluate", $"{session.Url} :: {CdpFormat.Safe(expression)}", success, "cdp");
        return JsonSerializer.Serialize(payload, PerceptionTools.Json);
    }
}
