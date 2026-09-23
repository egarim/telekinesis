using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Telekinesis.Pilot;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// Issue #65. The claim this brain makes is "the action schema is enforced by
/// construction, not by parsing" — so the test that matters is that EVERY answer
/// Jev is allowed to give survives <see cref="PilotAction.Validate"/>. The rest
/// pins the request shape against the documented API (criteria is an option→
/// description map, not a list).
/// </summary>
public class JevBrainTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Func<JsonObject, (HttpStatusCode, JsonObject)> _reply;
        public readonly List<JsonObject> Requests = [];
        public readonly List<string> AuthHeaders = [];

        public FakeApi(Func<JsonObject, (HttpStatusCode, JsonObject)> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Requests.Add(body);
            AuthHeaders.Add(request.Headers.Authorization?.ToString()
                ?? string.Join(' ', request.Headers.GetValues("Authorization")));
            var (status, reply) = _reply(body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static JsonObject Answer(string action, string? target = null, string? key = null,
        double confidence = 0.9, JsonObject? targetProbabilities = null)
    {
        var answers = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "choice", ["choice"] = action, ["confidence"] = confidence },
        };
        if (target is not null || targetProbabilities is not null)
        {
            var t = new JsonObject { ["type"] = "choice" };
            if (target is not null) t["choice"] = target;
            if (targetProbabilities is not null) t["probabilities"] = targetProbabilities;
            answers["target"] = t;
        }
        if (key is not null) answers["key"] = new JsonObject { ["type"] = "choice", ["choice"] = key };
        return new JsonObject { ["model"] = "jev-latest", ["answers"] = answers };
    }

    private static readonly IReadOnlyList<BrainOption> Targets =
    [
        new("c1", "Button \"Seven\""),
        new("c2", "Edit \"Display\" showing 14"),
    ];

    private static (JevBrain Brain, FakeApi Api) Brain(Func<JsonObject, (HttpStatusCode, JsonObject)> reply)
    {
        var api = new FakeApi(reply);
        return (new JevBrain(url: "https://api.example/v1/systemone", apiKey: "k", http: new HttpClient(api)), api);
    }

    private static async Task<PilotAction> DecideAsync(JsonObject answer)
    {
        var (brain, _) = Brain(_ => (HttpStatusCode.OK, answer));
        using var handle = brain;
        var (json, _) = await brain.DecideAsync("sys", "state", Targets);
        var action = PilotAction.Parse(json, out var error);
        Assert.Null(error);
        return action!;
    }

    /// <summary>The whole point: no answer Jev can give may be an invalid action.</summary>
    [Theory]
    [InlineData("click", "c1", null)]
    [InlineData("click", "c2", null)]
    [InlineData("press", "none", "enter")]
    [InlineData("scroll", "none", null)]
    [InlineData("wait", "none", null)]
    [InlineData("done", "none", null)]
    // Inconsistent pairs: the questions are answered independently, so a target
    // may arrive on an action that takes none, and vice versa.
    [InlineData("done", "c1", null)]
    [InlineData("scroll", "c2", "enter")]
    public async Task Every_answer_Jev_can_give_is_a_valid_action(string action, string target, string? key)
    {
        var parsed = await DecideAsync(Answer(action, target, key));
        Assert.Null(parsed.Validate(Targets.Select(t => t.Id).ToHashSet()));
    }

    [Fact]
    public async Task Click_with_no_target_chosen_falls_back_to_the_most_probable_candidate()
    {
        var parsed = await DecideAsync(Answer("click", target: JevBrain.NoTarget, targetProbabilities: new JsonObject
        {
            [JevBrain.NoTarget] = 0.5,
            ["c1"] = 0.2,
            ["c2"] = 0.3,
        }));
        Assert.Equal("click", parsed.Action);
        Assert.Equal("c2", parsed.Target);
    }

    [Fact]
    public async Task The_none_sentinel_never_escapes_as_a_target()
    {
        foreach (var action in new[] { "press", "scroll", "wait", "done" })
        {
            var parsed = await DecideAsync(Answer(action, JevBrain.NoTarget, "enter"));
            Assert.NotEqual(JevBrain.NoTarget, parsed.Target);
        }
    }

    [Fact]
    public async Task Press_carries_the_chosen_key_as_its_text()
    {
        var parsed = await DecideAsync(Answer("press", JevBrain.NoTarget, "ctrl+s"));
        Assert.Equal("press", parsed.Action);
        Assert.Equal("ctrl+s", parsed.Text);
    }

    [Fact]
    public async Task Confidence_rides_along_into_the_trace()
    {
        var (brain, _) = Brain(_ => (HttpStatusCode.OK, Answer("done", confidence: 0.42)));
        using var handle = brain;
        var (json, _) = await brain.DecideAsync("sys", "state", Targets);
        Assert.Equal(0.42, (double)JsonNode.Parse(json)!["confidence"]!);
    }

    [Fact]
    public async Task The_request_matches_the_documented_shape()
    {
        var (brain, api) = Brain(_ => (HttpStatusCode.OK, Answer("wait")));
        using var handle = brain;
        await brain.DecideAsync("ignored system prompt", "goal: add 7 and 7", Targets);

        var body = Assert.Single(api.Requests);
        Assert.Equal("jev-latest", (string?)body["model"]);
        Assert.Equal("goal: add 7 and 7", (string?)body["state"]);
        Assert.Equal("Bearer k", api.AuthHeaders[0]);

        // criteria is a map of option -> description (max 255 options), not a list.
        var target = body["questions"]!["target"]!;
        Assert.Equal("choice", (string?)target["type"]);
        var criteria = target["criteria"]!.AsObject();
        Assert.Equal("Button \"Seven\"", (string?)criteria["c1"]);
        Assert.True(criteria.ContainsKey(JevBrain.NoTarget));

        // `type` is never offered: Jev cannot generate the string it needs.
        Assert.DoesNotContain("type", body["questions"]!["action"]!["criteria"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public async Task With_no_candidates_there_is_no_target_question_at_all()
    {
        var (brain, api) = Brain(_ => (HttpStatusCode.OK, Answer("wait")));
        using var handle = brain;
        await brain.DecideAsync("sys", "state", []);
        Assert.Null(api.Requests[0]["questions"]!["target"]);
    }

    [Fact]
    public async Task A_rate_limit_is_retried_once_then_reported_with_its_status()
    {
        var calls = 0;
        var (brain, _) = Brain(_ =>
        {
            calls++;
            return ((HttpStatusCode)429, new JsonObject { ["error"] = "slow down" });
        });
        using var handle = brain;
        var e = await Assert.ThrowsAsync<HttpRequestException>(
            () => brain.DecideAsync("sys", "state", Targets));
        Assert.Equal(2, calls);
        Assert.Contains("429", e.Message);
    }

    [Fact]
    public async Task An_invalid_key_fails_immediately_without_a_retry()
    {
        var calls = 0;
        var (brain, _) = Brain(_ =>
        {
            calls++;
            return (HttpStatusCode.Unauthorized, new JsonObject());
        });
        using var handle = brain;
        var e = await Assert.ThrowsAsync<HttpRequestException>(
            () => brain.DecideAsync("sys", "state", Targets));
        Assert.Equal(1, calls);
        Assert.Contains(JevBrain.KeyEnvVar, e.Message);
    }

}

// Holes found by attacking the "every answer is valid" claim rather than
// restating it. Both were reachable before the fix.
public class JevBrainReachableHoles
{
    private sealed class Fake(JsonObject reply) : HttpMessageHandler
    {
        public JsonObject? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Request = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!.AsObject();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static JsonObject Reply(JsonObject answers) => new() { ["answers"] = answers };

    [Fact]
    public async Task With_no_candidates_click_is_not_even_offered()
    {
        // There is no target question when there are no candidates, so a "click"
        // answer could never be completed — it must not be on the menu.
        var fake = new Fake(Reply(new JsonObject
        {
            ["action"] = new JsonObject { ["choice"] = "wait" },
        }));
        using var brain = new JevBrain(url: "https://api.example", apiKey: "k", http: new HttpClient(fake));
        await brain.DecideAsync("sys", "state", []);
        Assert.DoesNotContain("click", fake.Request!["questions"]!["action"]!["criteria"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public async Task Click_with_no_target_and_no_probabilities_still_yields_a_valid_action()
    {
        // The candidate list is ranked, so the top entry is the defensible guess
        // when the reply carries nothing better. The alternative is an action the
        // loop rejects, which costs a retry and teaches the brain nothing.
        var fake = new Fake(Reply(new JsonObject
        {
            ["action"] = new JsonObject { ["choice"] = "click" },
            ["target"] = new JsonObject { ["choice"] = JevBrain.NoTarget },
        }));
        using var brain = new JevBrain(url: "https://api.example", apiKey: "k", http: new HttpClient(fake));
        var (json, _) = await brain.DecideAsync("sys", "state", [new("c1", "Button \"Seven\""), new("c2", "Button \"Eight\"")]);
        var action = PilotAction.Parse(json, out var error);
        Assert.Null(error);
        Assert.Equal("c1", action!.Target);
        Assert.Null(action.Validate(new HashSet<string> { "c1", "c2" }));
    }

    [Fact]
    public async Task A_press_answer_with_no_key_is_a_malformed_reply_not_a_silent_invalid_action()
    {
        var fake = new Fake(Reply(new JsonObject
        {
            ["action"] = new JsonObject { ["choice"] = "press" },
        }));
        using var brain = new JevBrain(url: "https://api.example", apiKey: "k", http: new HttpClient(fake));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.DecideAsync("sys", "state", [new("c1", "Button")]));
        Assert.Contains("key", e.Message);
    }
}

/// <summary>
/// The contract is served by more than one thing: open-jev runs /v1/systemone
/// from a local Gemma on Apple silicon and needs no key unless OPENJEV_API_KEY
/// is set. A brain that demands a key could not talk to it at all.
/// </summary>
public class JevBrainLocalServerTests
{
    private sealed class Server(Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(r);
            var (status, body) = reply(r);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task A_keyless_server_that_answers_health_is_usable()
    {
        var server = new Server(r => r.RequestUri!.AbsolutePath == "/health"
            ? (System.Net.HttpStatusCode.OK, """{"status":"ok"}""")
            : (System.Net.HttpStatusCode.NotFound, "{}"));
        using var brain = new JevBrain(url: JevBrain.LocalUrl, apiKey: "", http: new HttpClient(server));
        Assert.True(await brain.ProbeAsync());
        Assert.Equal("http://localhost:8000/health", server.Seen[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task No_key_means_no_Authorization_header_at_all()
    {
        var server = new Server(_ => (System.Net.HttpStatusCode.OK,
            """{"answers":{"action":{"choice":"wait"}}}"""));
        using var brain = new JevBrain(url: JevBrain.LocalUrl, apiKey: "", http: new HttpClient(server));
        await brain.DecideAsync("sys", "state", [new("c1", "Button")]);
        Assert.False(server.Seen[0].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Without_a_key_and_without_a_server_the_brain_is_not_usable()
    {
        var server = new Server(_ => (System.Net.HttpStatusCode.NotFound, "{}"));
        using var brain = new JevBrain(url: JevBrain.DefaultUrl, apiKey: "", http: new HttpClient(server));
        Assert.False(await brain.ProbeAsync());
    }

    [Fact]
    public async Task A_key_alone_is_enough_when_there_is_no_health_endpoint()
    {
        var server = new Server(_ => (System.Net.HttpStatusCode.NotFound, "{}"));
        using var brain = new JevBrain(url: JevBrain.DefaultUrl, apiKey: "k", http: new HttpClient(server));
        Assert.True(await brain.ProbeAsync());
    }
}

/// <summary>
/// Latency is linear in question count — each question is its own prefill and
/// batched pass (~430 ms on an M1 Max via open-jev). The `key` question is only
/// ever used by `press`, so paying for it on every step was 40 % of the cost of
/// a step for nothing. These pin the deferral.
/// </summary>
public class JevBrainQuestionCountTests
{
    private sealed class Counter(Func<int, string> reply) : HttpMessageHandler
    {
        public readonly List<JsonObject> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!.AsObject());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply(Requests.Count), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly IReadOnlyList<BrainOption> One = [new("c1", "Button \"Seven\"")];

    [Fact]
    public async Task A_normal_step_asks_once_and_never_asks_for_a_key()
    {
        var counter = new Counter(_ => """{"answers":{"action":{"choice":"click"},"target":{"choice":"c1"}}}""");
        using var brain = new JevBrain(url: JevBrain.LocalUrl, apiKey: "", http: new HttpClient(counter));
        await brain.DecideAsync("sys", "state", One);

        var request = Assert.Single(counter.Requests);
        var asked = request["questions"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(["action", "target"], asked);
    }

    [Fact]
    public async Task Only_a_press_pays_for_the_second_call()
    {
        var counter = new Counter(n => n == 1
            ? """{"answers":{"action":{"choice":"press"},"target":{"choice":"none"}}}"""
            : """{"answers":{"key":{"choice":"enter"}}}""");
        using var brain = new JevBrain(url: JevBrain.LocalUrl, apiKey: "", http: new HttpClient(counter));
        var (json, _) = await brain.DecideAsync("sys", "state", One);

        Assert.Equal(2, counter.Requests.Count);
        Assert.Equal(["key"], counter.Requests[1]["questions"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("enter", PilotAction.Parse(json, out _)!.Text);
    }
}
