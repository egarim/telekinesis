using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Read-only tools — "clairvoyant mode". These work without input-injection
/// permissions and are safe to expose on their own.
/// </summary>
[McpServerToolType]
public static class PerceptionTools
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_applications")]
    [Description("List all applications visible to the platform accessibility layer.")]
    public static async Task<string> ListApplications(BackendProvider provider, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var apps = await backend.ListApplicationsAsync(ct);
        return JsonSerializer.Serialize(apps, Json);
    }

    [McpServerTool(Name = "get_tree")]
    [Description("Get the accessibility tree of one application, depth-limited. Use small depths (2-4) and drill down; trees can be huge.")]
    public static async Task<string> GetTree(
        BackendProvider provider,
        [Description("Application id from list_applications.")] string applicationId,
        [Description("Maximum depth to descend (default 3).")] int maxDepth,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var tree = await backend.GetTreeAsync(applicationId, maxDepth <= 0 ? 3 : maxDepth, ct);
        return JsonSerializer.Serialize(tree, Json);
    }

    [McpServerTool(Name = "find_elements")]
    [Description("Search for elements by role and/or name substring, optionally within one application. Prefer this over dumping trees.")]
    public static async Task<string> FindElements(
        BackendProvider provider,
        [Description("Normalized role, e.g. Button, Edit, MenuItem. Empty for any.")] string? role,
        [Description("Case-insensitive substring of the element name. Empty for any.")] string? nameContains,
        [Description("Restrict to this application id; empty searches all.")] string? applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var query = new ElementQuery
        {
            ApplicationId = string.IsNullOrEmpty(applicationId) ? null : applicationId,
            Role = Enum.TryParse<AccessibleRole>(role, ignoreCase: true, out var r) ? r : null,
            NameContains = string.IsNullOrEmpty(nameContains) ? null : nameContains,
        };
        var results = await backend.FindElementsAsync(query, ct);
        return JsonSerializer.Serialize(results, Json);
    }

    [McpServerTool(Name = "read_element")]
    [Description("Read full current detail of one element by id. Fails if the element no longer exists — re-query, do not guess.")]
    public static async Task<string> ReadElement(
        BackendProvider provider,
        [Description("Element id from a previous query.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var element = await backend.ReadElementAsync(new ElementRef(elementId, applicationId), ct);
        return JsonSerializer.Serialize(element, Json);
    }

    [McpServerTool(Name = "get_focused")]
    [Description("Get the currently focused element and its application — cheap orientation call.")]
    public static async Task<string> GetFocused(BackendProvider provider, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var focused = await backend.GetFocusedAsync(ct);
        return focused is null ? "null" : JsonSerializer.Serialize(focused, Json);
    }
}
