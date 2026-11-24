using System.Text.Json.Nodes;
using Telekinesis.Sentiora;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// In-memory stand-in for the Sentiora Terminal runtime (issue #67). It reimplements
/// the parts of Sentiora's src/server.js and src/session.js that the adapter depends
/// on — the revision guard, the owner token, the 16-session cap, dimension validation
/// and the VERBATIM error strings — so the adapter's retry, clamping, ownership and
/// recycling logic can be tested deterministically with no Node.js in CI.
///
/// <para>This is a stub, not an integration: a green run of the tests using it proves
/// the adapter speaks the protocol correctly, NOT that the real runtime agrees.
/// <see cref="SentioraRealRuntimeTests"/> is the half that drives the real thing.</para>
/// </summary>
internal sealed class FakeSentioraTransport : ISentioraTransport
{
    private sealed class Fake(string id, string owner, int cols, int rows, string screen)
    {
        public readonly string Id = id;
        public readonly string Owner = owner;
        public int Cols = cols;
        public int Rows = rows;
        public string Screen = screen;
        public long Revision;
        public string State = "spawned";
        public bool Closed;
        public readonly List<string> Input = [];
    }

    private readonly Dictionary<string, Fake> _sessions = new();
    private bool _broken;

    /// <summary>Every method the adapter called, in order, including background waits.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The (owner, revision, text) triples presented to terminal.type — the
    /// evidence for the retry and ownership tests.</summary>
    public List<(string Owner, long Revision, string Text)> TypeCalls { get; } = [];

    /// <summary>Park the background poll loop here until a test calls <see cref="ReleaseWait"/>.
    /// A counting semaphore rather than a TaskCompletionSource: a release that lands while
    /// the loop is between two waits must not be lost, or the loop parks forever and the
    /// test times out instead of observing the state change it asked for.</summary>
    private readonly SemaphoreSlim _waitGate = new(0);

    public void ReleaseWait() => _waitGate.Release();

    /// <summary>Reject this many terminal.type calls with "Stale observation" AND bump
    /// the revision, exactly as a child painting mid-write would, so the adapter has to
    /// re-observe to make progress.</summary>
    public int ForcedStaleRejections { get; set; }

    public int SessionLimit { get; init; } = 16;
    public int ProtocolVersion { get; init; } = 1;
    public string? FailCreateWith { get; set; }

    /// <summary>Screen the runtime reports for a newly created session, so the
    /// create-time paint can be asserted without waiting on the poll loop.</summary>
    public string? InitialScreen { get; set; }

    /// <summary>Refuse the owner token on terminal.type even when it is correct, to
    /// prove an ownership failure is surfaced loudly and NOT retried.</summary>
    public bool RejectOwner { get; set; }

    public bool IsBroken => _broken;
    public string? BrokenReason => _broken ? "the fake transport was broken on purpose" : null;

    public int SessionCount => _sessions.Count;
    public string FirstSessionId => _sessions.Values.First().Id;
    public string OwnerOfFirstSession => _sessions.Values.First().Owner;

    /// <summary>The parameters of the most recent terminal.create — how a test sees the
    /// command/argv split and the clamped dimensions the adapter actually sent.</summary>
    public JsonNode? LastCreateParameters { get; private set; }

    public string? LastCloseSessionId { get; private set; }
    public string? LastCloseOwner { get; private set; }

    public void Break() => _broken = true;

    public void SetScreen(string screen, string? sessionId = null)
    {
        var fake = Resolve(sessionId);
        fake.Screen = screen;
        fake.Revision++;
        if (fake.State == "spawned") fake.State = "operational";
    }

    public void SetState(string state, string? sessionId = null) => Resolve(sessionId).State = state;

    public string ScreenOf(string? sessionId = null) => Resolve(sessionId).Screen;

    public long RevisionOf(string? sessionId = null) => Resolve(sessionId).Revision;

    public IReadOnlyList<string> InputOf(string? sessionId = null) => Resolve(sessionId).Input;

    private Fake Resolve(string? sessionId) =>
        sessionId is null
            ? _sessions.Values.First()
            : _sessions.TryGetValue(sessionId, out var f)
                ? f
                : throw SentioraErrors.ToException("Unknown session");

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct = default)
    {
        Calls.Add(method);
        if (_broken) throw new SentioraTransportException("the fake transport was broken on purpose");

        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["platform"] = "fakeos",
                    ["arch"] = "fakearch",
                    ["conptyDll"] = false,
                };

            case "terminal.create":
            {
                if (_sessions.Count >= SessionLimit) throw SentioraErrors.ToException("Session limit");
                if (FailCreateWith is not null) throw SentioraErrors.ToException(FailCreateWith);
                var cols = Int(parameters, "cols");
                var rows = Int(parameters, "rows");
                // Mirrors dimensions() in Sentiora's src/session.js: the runtime itself
                // refuses out-of-range values, so an adapter that failed to clamp first
                // shows up here rather than silently working.
                if (cols < 2 || cols > 300 || rows < 2 || rows > 150)
                    throw SentioraErrors.ToException("Invalid dimensions");
                var command = Str(parameters, "command");
                if (command.Length == 0) throw SentioraErrors.ToException("Invalid command");
                LastCreateParameters = parameters?.DeepClone();

                var fake = new Fake(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), cols, rows,
                    InitialScreen ?? string.Join('\n', Enumerable.Repeat(string.Empty, rows)));
                _sessions[fake.Id] = fake;
                var observation = Observe(fake);
                observation["owner"] = fake.Owner;
                return observation;
            }

            case "terminal.observe":
                return Observe(Resolve(Str(parameters, "sessionId")));

            case "terminal.type":
            {
                var fake = Resolve(Str(parameters, "sessionId"));
                var owner = Str(parameters, "owner");
                var revision = Long(parameters, "revision");
                var text = Str(parameters, "text");
                TypeCalls.Add((owner, revision, text));

                if (fake.Closed) throw SentioraErrors.ToException("Session not running");
                if (RejectOwner || owner != fake.Owner) throw SentioraErrors.ToException("Input ownership required");
                if (ForcedStaleRejections > 0)
                {
                    ForcedStaleRejections--;
                    // Simulate the child painting between our observation and our write:
                    // the revision moves, so only a re-observe can recover it.
                    fake.Revision++;
                    fake.Screen += "\npainted-by-child";
                    throw SentioraErrors.ToException("Stale observation");
                }
                if (revision != fake.Revision) throw SentioraErrors.ToException("Stale observation");
                if (text.Length > 65536) throw SentioraErrors.ToException("Invalid input");

                fake.Input.Add(text);
                fake.Revision++; // input() bumps the revision itself
                return new JsonObject { ["revision"] = fake.Revision };
            }

            case "terminal.wait":
            {
                var fake = Resolve(Str(parameters, "sessionId"));
                await _waitGate.WaitAsync(ct).ConfigureAwait(false);
                return Observe(fake);
            }

            case "terminal.resize":
            {
                var fake = Resolve(Str(parameters, "sessionId"));
                if (Str(parameters, "owner") != fake.Owner || fake.Closed)
                    throw SentioraErrors.ToException("Resize denied");
                var cols = Int(parameters, "cols");
                var rows = Int(parameters, "rows");
                if (cols < 2 || cols > 300 || rows < 2 || rows > 150)
                    throw SentioraErrors.ToException("Invalid dimensions");
                fake.Cols = cols;
                fake.Rows = rows;
                fake.Revision++;
                return Observe(fake);
            }

            case "terminal.close":
            {
                var fake = Resolve(Str(parameters, "sessionId"));
                LastCloseSessionId = Str(parameters, "sessionId");
                LastCloseOwner = Str(parameters, "owner");
                // terminal.close uses a DIFFERENT string than terminal.type for the same
                // failure ("Ownership required" vs "Input ownership required"); both must map.
                if (Str(parameters, "owner") != fake.Owner) throw SentioraErrors.ToException("Ownership required");
                fake.Closed = true;
                fake.State = "released";
                return JsonValue.Create(true);
            }

            default:
                throw SentioraErrors.ToException("Unknown method");
        }
    }

    private static JsonObject Observe(Fake fake) => new()
    {
        ["sessionId"] = fake.Id,
        ["pid"] = 4242,
        ["state"] = fake.State,
        ["revision"] = fake.Revision,
        ["running"] = fake.State is "spawned" or "operational",
        ["exit"] = null,
        ["cols"] = fake.Cols,
        ["rows"] = fake.Rows,
        ["writeErrors"] = 0,
        ["cursor"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["screen"] = fake.Screen,
        ["idleMilliseconds"] = 0,
    };

    private static string Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static int Int(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    /// <summary>Set when the adapter shut this host down, so reaping is observable.</summary>
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        _waitGate.Dispose();
    }
}
