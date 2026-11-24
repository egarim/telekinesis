namespace Telekinesis.Sentiora;

/// <summary>What a Sentiora error message means. The runtime reports EVERY failure
/// as JSON-RPC code -32000, so the message string is the only discriminator — brittle
/// by construction, and the reason this mapping is isolated here and unit-tested
/// against the exact strings in Sentiora's src/server.js and src/session.js.</summary>
internal enum SentioraErrorKind
{
    /// <summary>Our `revision` no longer matches; re-observe and retry.</summary>
    StaleObservation,

    /// <summary>Wrong or missing `owner` token. The adapter is the only holder, so
    /// this means a bug here — never retry it.</summary>
    OwnershipRequired,

    /// <summary>The child exited or the session was closed.</summary>
    SessionNotRunning,

    /// <summary>Bad id, or the host process restarted and lost every session.</summary>
    UnknownSession,

    /// <summary>The runtime hit its 16-session cap.</summary>
    SessionLimit,

    /// <summary>Outside cols 2-300 / rows 2-150. Should be unreachable: the adapter
    /// clamps first.</summary>
    InvalidDimensions,

    /// <summary>Resize refused (wrong owner, or the session is closed/exited).</summary>
    ResizeDenied,

    /// <summary>A caller-side argument error: bad command, key, input or wait.</summary>
    InvalidArgument,

    /// <summary>Anything else, including node-pty spawn failures.</summary>
    Other,
}

internal sealed class SentioraProtocolException(string message, string sentioraMessage) : Exception(message)
{
    /// <summary>The runtime's verbatim message — the only machine-readable part of
    /// the error, so it is preserved even though we do not parse it again.</summary>
    public string SentioraMessage { get; } = sentioraMessage;

    public SentioraErrorKind Kind { get; } = SentioraErrors.Map(sentioraMessage);

    /// <summary>The JSON-RPC error code. Always -32000 today; kept so a future
    /// runtime that adds distinct codes can be detected rather than assumed.</summary>
    public int ErrorCode { get; init; } = -32000;
}

internal static class SentioraErrors
{
    /// <summary>Map a runtime message onto a kind. Exact, case-sensitive matches:
    /// the strings below are the literals thrown by Sentiora at commit 8efb6e5.</summary>
    public static SentioraErrorKind Map(string? message) => message switch
    {
        "Stale observation" => SentioraErrorKind.StaleObservation,
        // terminal.type says "Input ownership required", terminal.close says
        // "Ownership required" — same failure, two call sites, one meaning.
        "Input ownership required" or "Ownership required" => SentioraErrorKind.OwnershipRequired,
        "Session not running" => SentioraErrorKind.SessionNotRunning,
        "Unknown session" => SentioraErrorKind.UnknownSession,
        "Session limit" => SentioraErrorKind.SessionLimit,
        "Invalid dimensions" => SentioraErrorKind.InvalidDimensions,
        "Resize denied" => SentioraErrorKind.ResizeDenied,
        "Invalid command" or "Invalid conptyDll" or "Invalid input"
            or "Invalid wait" or "Unknown key" or "Unknown method" => SentioraErrorKind.InvalidArgument,
        _ => SentioraErrorKind.Other,
    };

    public static SentioraProtocolException ToException(string message, int code = -32000)
    {
        var kind = Map(message);
        var text = kind switch
        {
            SentioraErrorKind.SessionLimit =>
                "The Sentiora console backend refused a new session because the runtime hit its "
                + "concurrent-session cap (16 per host process). Close a console session and retry.",
            SentioraErrorKind.UnknownSession =>
                "The Sentiora console backend no longer knows this session — the runtime restarted "
                + "or the session was released. Open a new one; session state does not survive it.",
            SentioraErrorKind.SessionNotRunning =>
                "The Sentiora console session's child process has exited, so it cannot be written to.",
            SentioraErrorKind.OwnershipRequired =>
                "The Sentiora console backend rejected the session's owner token. This is a Telekinesis "
                + "bug, not a runtime condition — the adapter is the only holder of that token.",
            SentioraErrorKind.InvalidDimensions =>
                "The Sentiora console backend rejected the terminal dimensions (it supports cols 2-300 "
                + "and rows 2-150). This should have been clamped before the call.",
            _ => $"The Sentiora console backend reported an error: {message}",
        };
        return new SentioraProtocolException(text, message) { ErrorCode = code };
    }
}

/// <summary>
/// Sentiora's dimension range, measured and enforced in its src/session.js
/// <c>dimensions()</c>: cols 2-300, rows 2-150. Telekinesis's own clamp is 2-1000,
/// so without this a 500-column console_resize would reach the runtime and come back
/// as "Invalid dimensions" — a hard failure where the caller expected a clamp.
/// Clamping here and reporting the applied size keeps console_resize's documented
/// contract ("the reply reports the size actually applied") true for this backend.
/// </summary>
internal static class SentioraDimensions
{
    public const int MinCols = 2, MaxCols = 300;
    public const int MinRows = 2, MaxRows = 150;

    public static int ClampCols(int cols) => Math.Clamp(cols, MinCols, MaxCols);
    public static int ClampRows(int rows) => Math.Clamp(rows, MinRows, MaxRows);
}

/// <summary>
/// Telekinesis takes one <c>shell</c> command line; Sentiora's terminal.create takes
/// a separate <c>command</c> and <c>args</c> array. Split on whitespace honouring
/// double quotes, the same convention Repl.Tokenize already uses, so
/// <c>"C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo</c> survives as one argv[0].
/// </summary>
internal static class SentioraCommandLine
{
    public static (string Command, IReadOnlyList<string> Args) Split(string shell)
    {
        var tokens = Tokenize(shell);
        return tokens.Count == 0
            ? throw new ArgumentException("A console session needs a non-empty command line.")
            : (tokens[0], tokens.GetRange(1, tokens.Count - 1));
    }

    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false; // so an empty quoted argument ("") is still a token
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }
}
