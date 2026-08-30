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
    [Description("Invoke an element's action. Default activates it (click a button, toggle a checkbox, select a list item). Pass action 'expand'/'collapse' to open or close a combo box or tree item.")]
    public static async Task<string> Invoke(
        BackendProvider provider,
        VisionMemoryService memoryService,
        [Description("Element id from a previous query.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        [Description("Optional specific action: invoke (default), expand, collapse, toggle, select.")] string? action,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var reference = new ElementRef(elementId, applicationId);
        var result = await backend.InvokeAsync(reference, action, ct);
        if (result.Success)
            await memoryService.LearnFromElementAsync(backend, reference, ct);
        return Audit(string.IsNullOrEmpty(action) ? "invoke" : action, elementId, result);
    }

    [McpServerTool(Name = "set_value")]
    [Description("Set a numeric element's value (slider, spinner, progress) via the native RangeValue pattern.")]
    public static async Task<string> SetValue(
        BackendProvider provider,
        string elementId, string applicationId,
        [Description("The numeric value to set, within the element's min/max.")] double value,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var result = await backend.SetValueAsync(new ElementRef(elementId, applicationId), value, ct);
        return Audit("set_value", elementId, result);
    }

    [McpServerTool(Name = "set_text")]
    [Description("Replace the text content of an editable element.")]
    public static async Task<string> SetText(
        BackendProvider provider,
        VisionMemoryService memoryService,
        string elementId, string applicationId,
        [Description("The full new text content.")] string text,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var reference = new ElementRef(elementId, applicationId);
        var result = await backend.SetTextAsync(reference, text, ct);
        if (result.Success)
            await memoryService.LearnFromElementAsync(backend, reference, ct);
        return Audit("set_text", elementId, result);
    }

    [McpServerTool(Name = "click")]
    [Description("Pointer-click the center of an element via input injection. Use invoke first; click is the fallback for apps without native actions.")]
    public static async Task<string> Click(
        BackendProvider provider,
        VisionMemoryService memoryService,
        string elementId, string applicationId,
        [Description("left, middle or right (default left).")] string? button,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var btn = Enum.TryParse<PointerButton>(button, ignoreCase: true, out var b) ? b : PointerButton.Left;
        var reference = new ElementRef(elementId, applicationId);
        var result = await backend.ClickAsync(reference, btn, ct);
        if (result.Success)
            await memoryService.LearnFromElementAsync(backend, reference, ct);
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

    [McpServerTool(Name = "click_at")]
    [Description("Pointer-click at raw screen coordinates via input injection. For vision-derived targets (parse_screen); prefer invoke/click on real elements when the accessibility tree works.")]
    public static async Task<string> ClickAt(
        BackendProvider provider,
        VisionMemoryService memoryService,
        [Description("Screen X in pixels.")] int x,
        [Description("Screen Y in pixels.")] int y,
        [Description("left, middle or right (default left).")] string? button,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (backend is not IPointerInjectionBackend pointer)
            throw new NotSupportedException($"{backend.Name} does not support coordinate clicks yet.");
        var btn = Enum.TryParse<PointerButton>(button, ignoreCase: true, out var b) ? b : PointerButton.Left;
        var result = await pointer.ClickAtAsync(x, y, btn, ct);
        if (result.Success)
        {
            // A vision-found element that just got used has proven itself — remember it.
            var anchor = memoryService.OnClickedAt(x, y);
            if (anchor is not null)
                Console.Error.WriteLine($"[telekinesis] learned anchor {anchor.Id} \"{anchor.Caption}\" for {anchor.AppKey}");
        }
        return Audit("click_at", $"({x},{y})", result);
    }

    [McpServerTool(Name = "navigate")]
    [Description("Navigate a browser to a URL: finds the address bar in the browser's chrome, focuses it, sets the URL and presses Enter. Convenience over find/click/set_text/press_keys.")]
    public static async Task<string> Navigate(
        BackendProvider provider,
        [Description("Browser application id (pid:N).")] string applicationId,
        [Description("The URL to load.")] string url,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);

        // The address bar is an editable Edit in the browser chrome; every major
        // browser names it with "address" (Edge "Search or enter web address",
        // Chrome "Address and search bar", Firefox "…or enter address").
        var edits = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = applicationId,
            Role = AccessibleRole.Edit,
            ExcludeDocumentContent = true,
            MaxResults = 10,
        }, ct);
        var bar = edits.FirstOrDefault(e =>
                      e.Name?.Contains("address", StringComparison.OrdinalIgnoreCase) == true)
                  ?? edits.FirstOrDefault(e => (e.States & ElementState.Editable) != 0)
                  ?? edits.FirstOrDefault();
        if (bar is null)
            return Audit("navigate", url, ActionResult.Failed(ActionPath.NativeAction,
                "No address bar found in the browser chrome — is this application a browser?"));

        // Click to focus (also fronts the browser), set the full URL, commit.
        var click = await backend.ClickAsync(bar.Ref, PointerButton.Left, ct);
        if (!click.Success) return Audit("navigate", url, click);
        await Task.Delay(150, ct); // let focus settle
        var set = await backend.SetTextAsync(bar.Ref, url, ct);
        if (!set.Success) return Audit("navigate", url, set);
        var enter = await backend.PressKeysAsync("enter", ct);
        return Audit("navigate", url, enter);
    }

    private static string Audit(string tool, string target, ActionResult result)
    {
        // Audit trail: every action lands on stderr (visible in MCP client logs)
        // AND in the state-dir audit file, regardless of outcome.
        Console.Error.WriteLine($"[telekinesis] {DateTimeOffset.Now:O} {tool} target={target} success={result.Success} path={result.Path}");
        AuditLog.Append(tool, target, result.Success, result.Path.ToString());
        return JsonSerializer.Serialize(result, PerceptionTools.Json);
    }
}
