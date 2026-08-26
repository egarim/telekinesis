using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Action tools — "telekinesis mode". Each tries the native accessibility action
/// first and falls back to OS input injection; the result reports which path ran.
/// Every call is written to the audit log. Disabled entirely in --read-only mode.
/// </summary>
[McpServerToolType]
public static class ActionTools
{
    [McpServerTool(Name = "invoke")]
    [Description("Invoke an element's default action (click a button, activate a menu item).")]
    public static async Task<string> Invoke(
        BackendProvider provider,
        [Description("Element id from a previous query.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await backend.InvokeAsync(new ElementRef(elementId, applicationId), ct: ct);
        return Audit("invoke", elementId, result);
    }

    [McpServerTool(Name = "set_text")]
    [Description("Replace the text content of an editable element.")]
    public static async Task<string> SetText(
        BackendProvider provider,
        string elementId, string applicationId,
        [Description("The full new text content.")] string text,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await backend.SetTextAsync(new ElementRef(elementId, applicationId), text, ct);
        return Audit("set_text", elementId, result);
    }

    [McpServerTool(Name = "click")]
    [Description("Pointer-click the center of an element via input injection. Use invoke first; click is the fallback for apps without native actions.")]
    public static async Task<string> Click(
        BackendProvider provider,
        string elementId, string applicationId,
        [Description("left, middle or right (default left).")] string? button,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var btn = Enum.TryParse<PointerButton>(button, ignoreCase: true, out var b) ? b : PointerButton.Left;
        var result = await backend.ClickAsync(new ElementRef(elementId, applicationId), btn, ct);
        return Audit("click", elementId, result);
    }

    [McpServerTool(Name = "type_text")]
    [Description("Type text into whatever currently has focus, via input injection.")]
    public static async Task<string> TypeText(BackendProvider provider, string text, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await backend.TypeTextAsync(text, ct);
        return Audit("type_text", "(focused)", result);
    }

    [McpServerTool(Name = "press_keys")]
    [Description("Press a key combination, e.g. 'ctrl+s', 'alt+F4', 'enter'.")]
    public static async Task<string> PressKeys(BackendProvider provider, string combination, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await backend.PressKeysAsync(combination, ct);
        return Audit("press_keys", combination, result);
    }

    private static string Audit(string tool, string target, ActionResult result)
    {
        // Audit log: every action lands on stderr (visible in MCP client logs)
        // regardless of outcome. TODO: also append to a file under XDG_STATE_HOME.
        Console.Error.WriteLine($"[telekinesis] {DateTimeOffset.Now:O} {tool} target={target} success={result.Success} path={result.Path}");
        return JsonSerializer.Serialize(result, PerceptionTools.Json);
    }
}
