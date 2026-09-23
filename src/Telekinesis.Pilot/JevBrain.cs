using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Telekinesis.Pilot;

/// <summary>
/// A "System 1" brain (issue #65): TypeSafe's Jev answers *enumerated* questions
/// rather than writing JSON. We hand it the same state the LLM brain sees plus a
/// `choice` question per field, and it returns one option from the set we defined
/// — so the action schema is enforced by construction, not by parsing.
///
/// Two consequences shape this class:
/// * <b>No text generation.</b> Jev cannot invent a string, so the `type` verb is
///   not offered at all — offering an action it can never complete would only
///   produce actions that <see cref="PilotAction.Validate"/> rejects. `press`
///   works because a key combination can be enumerated.
/// * <b>No instruction channel.</b> The `system` prompt is ignored; the task lives
///   in each question's `instructions`, which is where Jev takes direction.
/// </summary>
public sealed class JevBrain : ILocalBrain
{
    public const string KeyEnvVar = "TELEKINESIS_JEV_KEY";
    public const string UrlEnvVar = "TELEKINESIS_JEV_URL";
    public const string ModelEnvVar = "TELEKINESIS_JEV_MODEL";

    public const string DefaultUrl = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";

    /// <summary>Sentinel option for "this action needs no target" — a choice set
    /// cannot express "none of the above" on its own.</summary>
    public const string NoTarget = "none";

    /// <summary>The verbs this brain can actually carry out (no `type`, see above).</summary>
    private static readonly Dictionary<string, string> ActionCriteria = new()
    {
        ["click"] = "Activate one of the candidate elements (press a button, open a menu item, follow a link).",
        ["press"] = "Send a key combination to the application instead of clicking anything.",
        ["scroll"] = "Scroll the window down to bring more elements into view.",
        ["wait"] = "Do nothing this step; the application is still busy.",
        ["done"] = "The goal is already satisfied by what the readouts show. Do not guess: only choose this when the state proves it.",
    };

    /// <summary>Keys `press` may send. Enumerated because Jev picks from a set
    /// rather than writing one.</summary>
    private static readonly Dictionary<string, string> KeyCriteria = new()
    {
        ["enter"] = "Confirm, submit, or activate the focused element.",
        ["tab"] = "Move focus to the next element.",
        ["escape"] = "Dismiss a dialog, menu, or popup.",
        ["backspace"] = "Delete the character before the caret.",
        ["delete"] = "Delete the character after the caret, or the selection.",
        ["ctrl+a"] = "Select all.",
        ["ctrl+s"] = "Save.",
        ["ctrl+c"] = "Copy the selection.",
        ["ctrl+v"] = "Paste.",
        ["up"] = "Move up one item or line.",
        ["down"] = "Move down one item or line.",
        ["left"] = "Move left one item or character.",
        ["right"] = "Move right one item or character.",
        ["home"] = "Go to the start.",
        ["end"] = "Go to the end.",
    };

    /// <summary>Jev caps a choice at 255 options; leave room for the sentinel.</summary>
    private const int MaxTargets = 254;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _url;
    private readonly string _model;
    private readonly string? _key;

    public JevBrain(string? url = null, string? model = null, string? apiKey = null, HttpClient? http = null)
    {
        _url = (url ?? Environment.GetEnvironmentVariable(UrlEnvVar) ?? DefaultUrl).TrimEnd('/');
        _model = model ?? Environment.GetEnvironmentVariable(ModelEnvVar) ?? DefaultModel;
        _key = apiKey ?? Environment.GetEnvironmentVariable(KeyEnvVar);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Name => $"{_model} @ {_url}";

    /// <summary>
    /// Whether this brain is usable. Unlike Ollama there is no health endpoint,
    /// and a real request costs money — so this checks only that a key is
    /// configured. A wrong key still fails at the first decision with 401.
    /// </summary>
    public Task<bool> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(_key));

    public async Task<(string Json, int LatencyMs)> DecideAsync(
        string system, string user, IReadOnlyList<BrainOption> targets, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_key))
            throw new InvalidOperationException($"No Jev API key. Set {KeyEnvVar}.");

        // Offer only verbs that can actually be completed from here. With no
        // candidates there is no target question, so a "click" answer could never
        // be finished — same reasoning that keeps `type` off the menu entirely.
        var offered = targets.Take(MaxTargets).ToList();
        var actions = offered.Count > 0
            ? ActionCriteria
            : ActionCriteria.Where(kv => kv.Key != "click").ToDictionary(kv => kv.Key, kv => kv.Value);

        var questions = new JsonObject
        {
            ["action"] = Choice(
                "Choose the single next action that makes progress toward the goal, given the "
                + "current screen, the readouts, and what the previous action did.",
                actions),
            ["key"] = Choice(
                "If — and only if — the action is 'press', which key combination should be sent? "
                + "Ignored for every other action.",
                KeyCriteria),
        };

        // A choice needs something to choose between: with no candidates there is
        // no target question at all, rather than one whose only option is "none".
        if (offered.Count > 0)
        {
            var criteria = new Dictionary<string, string>(offered.Count + 1);
            foreach (var o in offered) criteria[o.Id] = o.Description;
            criteria[NoTarget] = "The chosen action acts on no particular element (press, scroll, wait, done).";
            questions["target"] = Choice(
                "Which candidate element should the action act on? Choose "
                + $"'{NoTarget}' when the action needs no element.",
                criteria);
        }

        var sw = Stopwatch.StartNew();
        using var response = await SendAsync(new JsonObject
        {
            ["model"] = _model,
            ["state"] = user,
            ["questions"] = questions,
        }, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return (Translate(body, offered), (int)sw.ElapsedMilliseconds);
    }

    private static JsonObject Choice(string instructions, IReadOnlyDictionary<string, string> criteria)
    {
        var map = new JsonObject();
        foreach (var (option, description) in criteria) map[option] = description;
        return new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = instructions,
            ["criteria"] = map,
        };
    }

    /// <summary>
    /// POST with one retry on the two statuses the API documents as transient
    /// (429 rate limit, 529 overloaded). Everything else — notably 401 and 422 —
    /// is a configuration error and fails immediately with its status.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(JsonObject payload, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            response?.Dispose();
            using var request = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("Authorization", $"Bearer {_key}");
            response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return response;
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)529)) break;
        }

        var status = (int)response!.StatusCode;
        var detail = status switch
        {
            401 => $"invalid API key (check {KeyEnvVar})",
            422 => "the request body was rejected: " + await response.Content.ReadAsStringAsync(ct),
            429 => "rate limited, and the retry was rate limited too",
            529 => "service overloaded, and the retry was too",
            _ => await response.Content.ReadAsStringAsync(ct),
        };
        response.Dispose();
        throw new HttpRequestException($"Jev returned {status}: {detail}");
    }

    /// <summary>
    /// Turn Jev's answers into the action JSON the loop already parses and traces.
    /// The confidence rides along: PilotAction ignores unknown fields, and the
    /// trace records the raw string — so calibration lands in the dataset for free.
    /// </summary>
    private static string Translate(JsonNode body, IReadOnlyList<BrainOption> offered)
    {
        var answers = body["answers"]?.AsObject()
            ?? throw new InvalidOperationException("Jev returned no answers.");

        string? Chosen(string question) => answers[question]?["choice"]?.GetValue<string>();

        var action = Chosen("action")
            ?? throw new InvalidOperationException("Jev returned no 'action' answer.");
        var target = Chosen("target");
        var result = new JsonObject { ["action"] = action };

        // Only the verbs that take one carry a target; `none` never leaves this class.
        // The questions are answered independently in one pass, so "click" paired
        // with "none" is possible — and would be rejected downstream as an action
        // with no target. Fall back to the most probable real option rather than
        // spending a retry: the distribution is already in the reply.
        if (action is "click")
        {
            // `click` is only offered when candidates exist, so the last resort is
            // always available: the list is RANKED, so its head is the best guess
            // the loop has — better than an action Validate rejects, which costs a
            // retry and teaches a System-1 brain nothing.
            target = target is null or NoTarget
                ? MostProbableTarget(answers["target"]) ?? offered[0].Id
                : target;
            result["target"] = target;
        }
        if (action is "press")
            result["text"] = Chosen("key")
                ?? throw new InvalidOperationException(
                    "Jev chose 'press' but returned no 'key' answer; the reply is malformed.");
        if (answers["action"]?["confidence"] is JsonNode confidence)
            result["confidence"] = confidence.DeepClone();

        return result.ToJsonString();
    }

    /// <summary>The highest-probability option that is an actual candidate, or null
    /// when the reply carries no usable distribution.</summary>
    private static string? MostProbableTarget(JsonNode? answer)
    {
        if (answer?["probabilities"] is not JsonObject probabilities) return null;
        string? best = null;
        var bestP = double.NegativeInfinity;
        foreach (var (option, value) in probabilities)
        {
            if (option == NoTarget || value is null) continue;
            var p = value.GetValue<double>();
            if (p <= bestP) continue;
            bestP = p;
            best = option;
        }
        return best;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
