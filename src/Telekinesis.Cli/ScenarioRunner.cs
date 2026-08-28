using System.Text.Json;
using System.Text.Json.Nodes;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// `telekinesis run <scenario.json>` — executes a demos/*.json scenario with
/// caption output for filming. Steps carry: say (caption), tool + args, optional
/// pre (setup sub-steps), bind (store result — first element for find_elements),
/// assert ("a.Path == b.Path", !=, or contains), expect (informational note).
/// {{binding.path}} placeholders in args resolve against earlier bind results.
/// Stops on first failure with a nonzero exit; native actions are the primary
/// path and injection the fallback, exactly like the tools themselves.
/// </summary>
internal static class ScenarioRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var file = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("Usage: telekinesis run <scenario.json> [--enable-actions]");
            return 2;
        }

        JsonObject scenario;
        try
        {
            scenario = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!.AsObject();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Malformed scenario: {ex.Message}");
            return 2;
        }

        var steps = scenario["steps"]?.AsArray() ?? [];
        var actionsEnabled = args.Contains("--enable-actions");
        if (!actionsEnabled && steps.SelectMany(Flatten).Any(s => IsActionTool((string?)s?["tool"])))
        {
            Console.Error.WriteLine("This scenario performs actions; refusing without --enable-actions (read-only by default).");
            return 2;
        }

        await using var provider = new BackendProvider();
        IAccessibilityBackend backend;
        try
        {
            backend = await provider.GetConnectedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connect failed: {ex.Message} (run `telekinesis doctor`)");
            return 1;
        }
        var memoryService = new VisionMemoryService();

        Console.WriteLine($"■ {scenario["name"]}");
        if (scenario["narration"] is { } narration) Console.WriteLine($"  {narration}\n");

        var bindings = new Dictionary<string, JsonNode?>();
        var index = 0;
        foreach (var stepNode in steps)
        {
            index++;
            var step = stepNode!.AsObject();
            if (step["say"] is { } say) Console.WriteLine($"▶ {say}");
            try
            {
                foreach (var pre in step["pre"]?.AsArray() ?? [])
                    await ExecuteAsync(pre!.AsObject(), bindings, provider, memoryService, backend, quiet: true);
                if (step["tool"] is not null)
                    await ExecuteAsync(step, bindings, provider, memoryService, backend, quiet: false);
                if (step["assert"] is { } assertion && !EvaluateAssert((string)assertion!, bindings, out var detail))
                {
                    Console.Error.WriteLine($"  ✗ assert failed: {assertion}   ({detail})");
                    return 1;
                }
                if (step["assert"] is { } a2) Console.WriteLine($"  ✓ assert: {a2}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ✗ step {index} failed: {ex.Message}");
                return 1;
            }
        }
        Console.WriteLine($"\n✓ {scenario["name"]}: all {index} step(s) passed.");
        return 0;
    }

    private static IEnumerable<JsonNode?> Flatten(JsonNode? step)
    {
        yield return step;
        foreach (var pre in step?["pre"]?.AsArray() ?? []) yield return pre;
    }

    private static bool IsActionTool(string? tool) => tool is "invoke" or "set_text" or "set_value" or "click"
        or "type_text" or "press_keys" or "click_at" or "fill_credential";

    private static async Task ExecuteAsync(JsonObject step, Dictionary<string, JsonNode?> bindings,
        BackendProvider provider, VisionMemoryService memoryService, IAccessibilityBackend backend, bool quiet)
    {
        var tool = (string)step["tool"]!;
        var raw = step["args"]?.AsObject() ?? [];
        var a = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in raw)
            a[k] = v is JsonValue val && val.TryGetValue<string>(out var s)
                ? JsonValue.Create(Substitute(s, bindings))
                : v?.DeepClone();

        string? S(string key) => a.TryGetValue(key, out var v) ? (string?)v?.AsValue() : null;
        int I(string key, int fallback = 0) => a.TryGetValue(key, out var v) && v is not null ? (int)v.AsValue() : fallback;

        var result = tool switch
        {
            "find_elements" => await PerceptionTools.FindElements(provider, S("role"), S("nameContains"), S("applicationId"), default),
            "read_element" => await PerceptionTools.ReadElement(provider, S("elementId")!, S("applicationId")!, default),
            "get_focused" => await PerceptionTools.GetFocused(provider, default),
            "wait_for" => await PerceptionTools.WaitFor(provider, S("kind") ?? "", I("timeoutMs", 2000), default),
            "highlight" => await PerceptionTools.Highlight(provider, S("elementId"), S("applicationId"), S("region"), S("label"), I("durationMs"), default),
            "assert_element" => await AssertTools.AssertElement(provider, S("role"), S("nameContains"), S("applicationId"), S("mustBe"), I("timeoutMs", 3000), default),
            "invoke" => await ActionTools.Invoke(provider, S("elementId")!, S("applicationId")!, S("action"), default),
            "set_text" => await ActionTools.SetText(provider, S("elementId")!, S("applicationId")!, S("text") ?? "", default),
            "set_value" => await ActionTools.SetValue(provider, S("elementId")!, S("applicationId")!, (double)a["value"]!.AsValue(), default),
            "click" => await ActionTools.Click(provider, S("elementId")!, S("applicationId")!, S("button"), default),
            "type_text" => await ActionTools.TypeText(provider, S("text") ?? "", default),
            "press_keys" => await ActionTools.PressKeys(provider, S("combination") ?? "", default),
            "click_at" => await ActionTools.ClickAt(provider, memoryService, I("x"), I("y"), S("button"), default),
            "fill_credential" => await CredentialTools.FillCredential(provider, S("elementId")!, S("applicationId")!, S("field") ?? "password", default),
            _ => throw new NotSupportedException($"Unknown tool '{tool}'."),
        };

        var node = JsonNode.Parse(result == "null" ? "null" : result);

        // Result health: action tools report Success; assert_element reports Ok;
        // a find that a later step depends on must not be empty.
        if (node is JsonObject o)
        {
            if (o["Success"] is { } ok && !(bool)ok.AsValue())
                throw new InvalidOperationException($"{tool} failed: {o["Error"]}");
            if (o["Ok"] is { } assertOk && !(bool)assertOk.AsValue())
                throw new InvalidOperationException($"{tool}: condition not met within {o["WaitedMs"]} ms");
        }

        if (step["bind"] is { } bindName)
        {
            var bound = node is JsonArray arr
                ? arr.Count > 0 ? arr[0]!.DeepClone() : throw new InvalidOperationException($"{tool} matched nothing to bind as '{bindName}'.")
                : node?.DeepClone();
            bindings[(string)bindName!] = bound;
        }

        if (!quiet)
        {
            var summary = node switch
            {
                JsonArray arr => $"{arr.Count} match(es)",
                JsonObject obj when obj["Success"] is not null =>
                    $"success path={((int?)obj["Path"]?.AsValue() == 0 ? "NativeAction" : "InputInjection")}",
                JsonObject obj when obj["Ok"] is not null => $"ok in {obj["WaitedMs"]} ms",
                _ => "done",
            };
            Console.WriteLine($"  ✓ {tool}: {summary}");
        }
    }

    /// <summary>Replaces every {{binding.path}} in a string argument.</summary>
    private static string Substitute(string value, Dictionary<string, JsonNode?> bindings)
    {
        var result = value;
        int start;
        while ((start = result.IndexOf("{{", StringComparison.Ordinal)) >= 0)
        {
            var end = result.IndexOf("}}", start, StringComparison.Ordinal);
            if (end < 0) break;
            var path = result[(start + 2)..end].Trim();
            result = result[..start] + ResolvePath(path, bindings) + result[(end + 2)..];
        }
        return result;
    }

    private static string ResolvePath(string path, Dictionary<string, JsonNode?> bindings)
    {
        var parts = path.Split('.');
        if (!bindings.TryGetValue(parts[0], out var node))
            throw new InvalidOperationException($"Unknown binding '{parts[0]}' in '{{{{{path}}}}}'.");
        foreach (var part in parts.Skip(1))
            node = node?[part];
        return node switch
        {
            null => throw new InvalidOperationException($"Path '{path}' resolved to null."),
            JsonValue v => v.ToString(),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>Assertions of the form `a.Path == b.Path`, `!=`, or `contains`; either
    /// side may be a binding path or a 'quoted literal'.</summary>
    private static bool EvaluateAssert(string expression, Dictionary<string, JsonNode?> bindings, out string detail)
    {
        foreach (var op in new[] {"!=", "==", " contains "})
        {
            var i = expression.IndexOf(op, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var left = Operand(expression[..i].Trim(), bindings);
            var right = Operand(expression[(i + op.Length)..].Trim(), bindings);
            detail = $"left='{left}' right='{right}'";
            return op.Trim().ToLowerInvariant() switch
            {
                "!=" => !string.Equals(left, right, StringComparison.Ordinal),
                "==" => string.Equals(left, right, StringComparison.Ordinal),
                _ => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            };
        }
        throw new ArgumentException($"Unsupported assertion '{expression}' (use ==, != or contains).");
    }

    private static string Operand(string token, Dictionary<string, JsonNode?> bindings) =>
        token.StartsWith('\'') && token.EndsWith('\'') ? token[1..^1]
        : token.StartsWith('"') && token.EndsWith('"') ? token[1..^1]
        : ResolvePath(token, bindings);
}
