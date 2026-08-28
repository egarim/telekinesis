using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Assertion tools — semantic UI tests. Poll the accessibility tree until a
/// condition holds or a timeout passes. Read-only; available in --read-only mode.
/// </summary>
[McpServerToolType]
public static class AssertTools
{
    public sealed record AssertResult(bool Ok, AccessibleElement? Matched, int WaitedMs);

    [McpServerTool(Name = "assert_element")]
    [Description("Wait until an element matching role/name exists (or is visible/enabled), up to a timeout. Use after an action to verify its effect. Returns {ok, matched, waitedMs}.")]
    public static async Task<string> AssertElement(
        BackendProvider provider,
        [Description("Normalized role, e.g. Button, Edit. Empty for any.")] string? role,
        [Description("Case-insensitive substring of the element name. Empty for any.")] string? nameContains,
        [Description("Restrict to this application id; empty searches all.")] string? applicationId,
        [Description("Condition: exists (default), visible, or enabled.")] string? mustBe,
        [Description("How long to keep polling, in ms (default 3000).")] int timeoutMs,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await RunAsync(backend, role, nameContains, applicationId, mustBe, timeoutMs, ct);
        return JsonSerializer.Serialize(result, PerceptionTools.Json);
    }

    /// <summary>Shared by the MCP tool, the `telekinesis assert` subcommand, and the scenario runner.</summary>
    public static async Task<AssertResult> RunAsync(
        IAccessibilityBackend backend, string? role, string? nameContains, string? applicationId,
        string? mustBe, int timeoutMs, CancellationToken ct = default)
    {
        var condition = string.IsNullOrEmpty(mustBe) ? "exists" : mustBe.ToLowerInvariant();
        if (condition is not ("exists" or "visible" or "enabled"))
            throw new ArgumentException($"Unknown condition '{mustBe}' (use exists, visible, or enabled).");

        var query = new ElementQuery
        {
            ApplicationId = string.IsNullOrEmpty(applicationId) ? null : applicationId,
            Role = Enum.TryParse<AccessibleRole>(role, ignoreCase: true, out var r) ? r : null,
            NameContains = string.IsNullOrEmpty(nameContains) ? null : nameContains,
            MaxResults = 10,
        };

        var deadline = TimeSpan.FromMilliseconds(timeoutMs <= 0 ? 3000 : timeoutMs);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var matches = await backend.FindElementsAsync(query, ct);
            var hit = matches.FirstOrDefault(m => condition switch
            {
                "visible" => (m.States & ElementState.Visible) != 0 && (m.States & ElementState.Offscreen) == 0,
                "enabled" => (m.States & ElementState.Enabled) != 0,
                _ => true,
            });
            if (hit is not null)
                return new AssertResult(true, hit, (int)sw.ElapsedMilliseconds);
            if (sw.Elapsed >= deadline)
                return new AssertResult(false, null, (int)sw.ElapsedMilliseconds);
            await Task.Delay(250, ct);
        }
    }
}
