using System.Text.Json;
using System.Text.Json.Nodes;

namespace Telekinesis.Pilot;

/// <summary>
/// The strict action schema (issue #10): the brain emits exactly one structured
/// action per step, never free-form instructions.
/// </summary>
/// <param name="Action">click | type | press | scroll | wait | done</param>
/// <param name="Target">Candidate id (c1, c2, …) for click/type; null otherwise.</param>
/// <param name="Text">Text for type, key combination for press, otherwise null.</param>
public sealed record PilotAction(string Action, string? Target, string? Text)
{
    public static readonly string[] Actions = ["click", "type", "press", "scroll", "wait", "done"];

    /// <summary>JSON Schema handed to the model as a hard output constraint.</summary>
    public static JsonNode Schema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Actions.Select(a => JsonValue.Create(a))]) },
            ["target"] = new JsonObject { ["type"] = "string" },
            ["text"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("action"),
    };

    /// <summary>Parse the model output; returns null with a reason when malformed.</summary>
    public static PilotAction? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            var node = JsonNode.Parse(Extract(json))!.AsObject();
            return new PilotAction(
                Action: ((string?)node["action"])?.ToLowerInvariant() ?? "",
                Target: (string?)node["target"],
                Text: (string?)node["text"]);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            error = $"not valid JSON: {e.Message}";
            return null;
        }
    }

    /// <summary>Tolerate models that wrap JSON in fences or prose.</summary>
    private static string Extract(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }

    /// <summary>
    /// Validation gate (issue #10 acceptance): the model can only target visible,
    /// valid candidate ids; required fields must be present. Returns the reason
    /// when the action is rejected — fed back to the model for one retry.
    /// </summary>
    public string? Validate(IReadOnlySet<string> candidateIds)
    {
        if (!Actions.Contains(Action)) return $"unknown action '{Action}'";
        switch (Action)
        {
            case "click" or "type":
                if (string.IsNullOrEmpty(Target)) return $"'{Action}' requires a target candidate id";
                if (!candidateIds.Contains(Target)) return $"target '{Target}' is not one of the current candidate ids";
                if (Action == "type" && string.IsNullOrEmpty(Text)) return "'type' requires text";
                break;
            case "press":
                if (string.IsNullOrEmpty(Text)) return "'press' requires text (a key combination like 'enter' or 'ctrl+s')";
                break;
        }
        return null;
    }
}
