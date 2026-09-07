using System.Text.Json;
using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The CDP tier's safety properties, proven without a browser: every test drives
/// CdpSession.Ingest / Fault or the pure CdpFormat projection directly.
/// </summary>
public class CdpTests
{
    private static JsonElement Frame(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- URL projection ----

    [Theory]
    [InlineData("https://a.b/c?access_token=eyJabc&page=2", "https://a.b/c?access_token=[redacted]&page=[redacted]")]
    [InlineData("https://a.b/c#id_token=zzz", "https://a.b/c")]                 // fragment dropped entirely
    [InlineData("https://user:pw@a.b/c", "https://a.b/c")]                      // userinfo dropped
    [InlineData("https://a.b:8443/x/y", "https://a.b:8443/x/y")]                // non-default port kept
    [InlineData("https://a.b/plain", "https://a.b/plain")]
    public void ProjectUrl_keeps_shape_and_drops_secrets(string raw, string expected)
        => Assert.Equal(expected, CdpFormat.ProjectUrl(raw));

    [Fact]
    public void ProjectUrl_summarizes_data_uris()
        => Assert.StartsWith("data:text/html,[", CdpFormat.ProjectUrl("data:text/html,<h1>hi</h1>"));

    // ---- text scrubbing ----

    [Theory]
    [InlineData("token is eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.abcdefghij", "eyJhbGci")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwx", "abcdefghijklmnop")]
    [InlineData("key sk-abcdefghijklmnopqrstuvwx", "sk-abcdefghijklmnop")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz012345", "ghp_abcdefghij")]
    [InlineData("xoxb-1234567890-abcdef", "xoxb-1234567890")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AIzaBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "AIzaBBBB")] // AIza + exactly 35
    [InlineData("sk_live_abcdefghijklmnopqrst", "sk_live_abcdefghij")]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("api_key: abc123xyz", "abc123xyz")]
    public void Scrub_removes_known_secret_shapes(string text, string secretFragment)
    {
        var scrubbed = CdpFormat.Scrub(text);
        Assert.DoesNotContain(secretFragment, scrubbed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_leaves_ordinary_text_alone()
        => Assert.Equal("rendered 42 rows in 13ms", CdpFormat.Scrub("rendered 42 rows in 13ms"));

    [Fact]
    public void Scrub_projects_urls_embedded_in_message_text()
    {
        // Live-observed leak: a CORS/fetch failure quotes the whole request URL,
        // so a token in the query reaches the model through the MESSAGE, not a url field.
        var scrubbed = CdpFormat.Scrub(
            "Access to fetch at 'https://api.example.com/v1?access_token=opaquevalue123&x=1' from origin 'null' blocked");
        Assert.DoesNotContain("opaquevalue123", scrubbed, StringComparison.Ordinal);
        Assert.Contains("access_token=[redacted]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/v1", scrubbed, StringComparison.Ordinal); // still diagnosable
    }

    // ---- RemoteObject flattening ----

    [Fact]
    public void Flatten_never_leaks_an_object_id()
    {
        var flat = CdpFormat.Flatten(Frame("""
            {"type":"object","className":"Object","objectId":"7.1.2",
             "preview":{"type":"object","description":"Object","overflow":false,
                        "properties":[{"name":"a","type":"number","value":"1"}]}}
            """));
        Assert.DoesNotContain("7.1.2", flat, StringComparison.Ordinal);
        Assert.Contains("a: 1", flat, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"string","value":"hi"}""", "hi")]
    [InlineData("""{"type":"number","value":42}""", "42")]
    [InlineData("""{"type":"undefined"}""", "undefined")]
    [InlineData("""{"type":"object","subtype":"null","value":null}""", "null")]
    public void Flatten_renders_primitives(string json, string expected)
        => Assert.Equal(expected, CdpFormat.Flatten(Frame(json)));

    // ---- ring buffer ----

    [Fact]
    public void Ring_keeps_the_newest_entries_and_reports_evictions()
    {
        var ring = new Ring<string>(200);
        string? lastEvicted = null;
        for (var i = 0; i < 300; i++) lastEvicted = ring.Add($"m{i}") ?? lastEvicted;

        Assert.Equal(200, ring.Count);
        var snapshot = ring.Snapshot(10);
        Assert.Equal("m299", snapshot[0]);           // newest first
        Assert.Equal("m290", snapshot[9]);
        Assert.NotNull(lastEvicted);                  // eviction is reported so indexes can be cleaned
        Assert.DoesNotContain("m99", ring.Snapshot(200));
    }

    // ---- frame routing ----

    [Fact]
    public async Task Reply_frames_complete_the_matching_call_only()
    {
        var session = new CdpSession("t1", "https://x/");
        var a = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RegisterForTest(1, a);
        session.RegisterForTest(2, b);

        session.Ingest(Frame("""{"id":1,"result":{"ok":true}}"""));
        Assert.True((await a.Task.WaitAsync(TimeSpan.FromSeconds(1))).GetProperty("ok").GetBoolean());
        Assert.False(b.Task.IsCompleted);            // the other call is untouched

        session.Ingest(Frame("""{"id":2,"error":{"code":-32000,"message":"boom"}}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Console_events_land_in_the_ring_scrubbed()
    {
        var session = new CdpSession("t1", "https://x/");
        session.Ingest(Frame("""
            {"method":"Runtime.consoleAPICalled","params":{
              "type":"warning","timestamp":1757000000000,
              "args":[{"type":"string","value":"token=ghp_abcdefghijklmnopqrstuvwxyz012345"}],
              "stackTrace":{"callFrames":[{"url":"https://x/app.js?sig=abc","lineNumber":118}]}}}
            """));
        var entry = Assert.Single(session.Console.Snapshot(10));
        Assert.Equal("warning", entry.Level);                     // CDP says "warning", not "warn"
        Assert.DoesNotContain("ghp_abcdef", entry.Text, StringComparison.Ordinal);
        Assert.Equal(119, entry.Line);                            // 0-based in CDP, 1-based for humans
        Assert.Equal("https://x/app.js?sig=[redacted]", entry.Url);
    }

    [Fact]
    public void Unknown_console_types_do_not_throw_or_drop()
    {
        var session = new CdpSession("t1", "https://x/");
        foreach (var type in new[] { "startGroup", "profile", "count", "clear", "table" })
            session.Ingest(Frame(
                """{"method":"Runtime.consoleAPICalled","params":{"type":"TYPE","args":[{"type":"string","value":"x"}]}}"""
                    .Replace("TYPE", type)));
        Assert.Equal(5, session.Console.Count);
        Assert.All(session.Console.Snapshot(5), e => Assert.Equal("info", e.Level));
    }

    [Fact]
    public void Exception_uses_the_description_not_the_useless_text()
    {
        var session = new CdpSession("t1", "https://x/");
        session.Ingest(Frame("""
            {"method":"Runtime.exceptionThrown","params":{"timestamp":1757000000000,
              "exceptionDetails":{"text":"Uncaught","lineNumber":9,
                "exception":{"type":"object","className":"TypeError",
                             "description":"TypeError: x is not a function"}}}}
            """));
        var entry = Assert.Single(session.Console.Snapshot(10));
        Assert.Equal("error", entry.Level);
        Assert.Contains("x is not a function", entry.Text, StringComparison.Ordinal);
        Assert.Equal(10, entry.Line);
    }

    // ---- the property the security review demands ----

    [Fact]
    public void Network_ingest_never_surfaces_headers_bodies_or_url_secrets()
    {
        var session = new CdpSession("t1", "https://x/");
        session.Ingest(Frame("""
            {"method":"Network.requestWillBeSent","params":{
              "requestId":"R1","type":"XHR","wallTime":1757000000.5,"timestamp":1000.0,
              "request":{"url":"https://api.example.com/v1/me?access_token=eyJabcdefghij",
                         "method":"POST",
                         "headers":{"Cookie":"session=hunter2","Authorization":"Bearer sekrit-token-value"},
                         "postData":"password=hunter2&user=joche"}}}
            """));
        session.Ingest(Frame("""
            {"method":"Network.responseReceived","params":{"requestId":"R1","timestamp":1000.4,
              "response":{"status":200,"mimeType":"application/json",
                          "headers":{"Set-Cookie":"session=hunter2; HttpOnly"}}}}
            """));
        session.Ingest(Frame("""{"method":"Network.loadingFinished","params":{"requestId":"R1","timestamp":1000.5,"encodedDataLength":1234}}"""));

        var entry = Assert.Single(session.Network.Snapshot(10));
        Assert.Equal(200, entry.Status);
        Assert.Equal(1234, entry.Size);
        Assert.Equal("POST", entry.Method);
        Assert.True(entry.DurationMs >= 400 && entry.DurationMs <= 600);  // monotonic delta, ms

        // The whole serialized payload must be free of every secret in the frames.
        var json = JsonSerializer.Serialize(session.Network.Snapshot(10), PerceptionTools.Json);
        foreach (var secret in new[] { "hunter2", "Cookie", "Authorization", "Set-Cookie", "eyJabcdefghij", "sekrit" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access_token=[redacted]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Redirect_hops_are_kept_as_separate_entries()
    {
        var session = new CdpSession("t1", "https://x/");
        session.Ingest(Frame("""
            {"method":"Network.requestWillBeSent","params":{"requestId":"R1","type":"Document",
              "wallTime":1757000000,"timestamp":1.0,"request":{"url":"https://a.test/one","method":"GET"}}}
            """));
        session.Ingest(Frame("""
            {"method":"Network.requestWillBeSent","params":{"requestId":"R1","type":"Document",
              "wallTime":1757000001,"timestamp":2.0,"redirectResponse":{"status":302},
              "request":{"url":"https://a.test/two","method":"GET"}}}
            """));
        var urls = session.Network.Snapshot(10).Select(e => e.Url).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains("https://a.test/one", urls);
        Assert.Contains("https://a.test/two", urls);
    }

    [Fact]
    public async Task Fault_completes_every_pending_call_instead_of_hanging()
    {
        var session = new CdpSession("t1", "https://x/");
        var calls = new List<Task<JsonElement>>();
        for (var i = 1; i <= 3; i++)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.RegisterForTest(i, tcs);
            calls.Add(tcs.Task);
        }

        session.Fault(new IOException("socket died"));

        foreach (var call in calls)
            await Assert.ThrowsAsync<InvalidOperationException>(() => call.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
