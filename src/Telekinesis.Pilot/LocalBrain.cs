using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Telekinesis.Pilot;

/// <summary>A step-policy model: JSON state in, one JSON action out.</summary>
public interface ILocalBrain : IDisposable
{
    string Name { get; }
    Task<(string Json, int LatencyMs)> DecideAsync(string system, string user, CancellationToken ct = default);
}

/// <summary>
/// Ollama-backed brain (default qwen3:4b-instruct at localhost:11434, override
/// with TELEKINESIS_BRAIN_URL / TELEKINESIS_BRAIN_MODEL — any machine on the
/// LAN running Ollama works). Output is hard-constrained to the action schema
/// via Ollama's structured-output `format`, temperature 0 for determinism.
/// </summary>
public sealed class OllamaBrain : ILocalBrain
{
    public const string UrlEnvVar = "TELEKINESIS_BRAIN_URL";
    public const string ModelEnvVar = "TELEKINESIS_BRAIN_MODEL";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly string _base;
    private readonly string _model;

    public OllamaBrain(string? baseUrl = null, string? model = null)
    {
        _base = (baseUrl
            ?? Environment.GetEnvironmentVariable(UrlEnvVar)
            ?? "http://localhost:11434").TrimEnd('/');
        _model = model
            ?? Environment.GetEnvironmentVariable(ModelEnvVar)
            ?? "qwen3:4b-instruct";
    }

    public string Name => $"{_model} @ {_base}";

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var r = await _http.GetAsync($"{_base}/api/tags", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<(string Json, int LatencyMs)> DecideAsync(string system, string user, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync($"{_base}/api/chat", new
        {
            model = _model,
            stream = false,
            format = PilotAction.Schema,
            options = new { temperature = 0 },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        }, ct);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var content = (string?)body["message"]?["content"]
            ?? throw new InvalidOperationException("Brain returned no message content.");
        return (content, (int)sw.ElapsedMilliseconds);
    }

    public void Dispose() => _http.Dispose();
}
