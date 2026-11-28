namespace Telekinesis.Sentiora;

/// <summary>How to launch the runtime: an executable plus its argument vector.</summary>
internal sealed record SentioraLaunchSpec(string FileName, IReadOnlyList<string> Arguments, string Source)
{
    /// <summary>A human-readable form for logs. A command line is not a secret, and
    /// attributing a run to the exact runtime that served it is the point of #67.</summary>
    public string Describe() => Arguments.Count == 0
        ? FileName
        : $"{FileName} {string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
}

internal sealed class SentioraNotFoundException(string message) : Exception(message);

/// <summary>
/// Runtime discovery (issue #67). Nothing here depends on Sentiora being installed:
/// the answer is either a launch spec or a <see cref="SentioraNotFoundException"/>
/// that says exactly what was tried, so the caller can announce the fallback instead
/// of degrading silently — #67 requires it to be unambiguous which backend is active.
/// </summary>
internal static class SentioraDiscovery
{
    /// <summary>Opt-in switch, mirroring the existing <c>TELEKINESIS_CONPTY=1</c> gate.
    /// Default off: with this unset Telekinesis behaves exactly as before.</summary>
    public const string EnableVariable = "TELEKINESIS_SENTIORA";

    /// <summary>An explicit command line, e.g. <c>node C:\src\sentiora-terminal\src\server.js</c>.</summary>
    public const string CommandVariable = "TELEKINESIS_SENTIORA_CMD";

    /// <summary>A path to the runtime's <c>server.js</c>, run under Node.</summary>
    public const string ServerVariable = "TELEKINESIS_SENTIORA_SERVER";

    /// <summary>The Node executable to run <see cref="ServerVariable"/> with. Default <c>node</c>.</summary>
    public const string NodeVariable = "TELEKINESIS_SENTIORA_NODE";

    /// <summary>The executable looked for on PATH when no variable is set.</summary>
    public const string PathCommand = "sentiora-terminal";

    public static bool IsEnabled(Func<string, string?>? env = null) =>
        (env ?? Environment.GetEnvironmentVariable)(EnableVariable) == "1";

    public static SentioraLaunchSpec Resolve(
        Func<string, string?>? env = null,
        Func<string, bool>? fileExists = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;

        // 1. An explicit command line wins: it is the only form that can express a
        //    runtime needing unusual arguments, and the one a repro will use.
        var cmd = env(CommandVariable);
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            var tokens = SentioraCommandLine.Tokenize(cmd);
            if (tokens.Count == 0)
                throw new SentioraNotFoundException($"{CommandVariable} is set but contains no command.");
            return new SentioraLaunchSpec(tokens[0], tokens.GetRange(1, tokens.Count - 1), CommandVariable);
        }

        // 2. A path to server.js plus a Node executable.
        var server = env(ServerVariable);
        if (!string.IsNullOrWhiteSpace(server))
        {
            if (!fileExists(server))
                throw new SentioraNotFoundException(
                    $"{ServerVariable} points at \"{server}\", which does not exist.");
            var node = env(NodeVariable);
            return new SentioraLaunchSpec(
                string.IsNullOrWhiteSpace(node) ? "node" : node, [server], ServerVariable);
        }

        // 3. `sentiora-terminal` on PATH — the zero-configuration case.
        var onPath = FindOnPath(PathCommand, env, fileExists);
        if (onPath is not null) return new SentioraLaunchSpec(onPath, [], "PATH");

        throw new SentioraNotFoundException(
            "the Sentiora Terminal runtime was not found. Set "
            + $"{CommandVariable} to a command line (e.g. \"node /path/to/sentiora-terminal/src/server.js\"), "
            + $"or {ServerVariable} to its server.js, or put `{PathCommand}` on PATH. "
            + "See https://github.com/egarim/sentiora-terminal");
    }

    private static string? FindOnPath(string command, Func<string, string?> env, Func<string, bool> fileExists)
    {
        var path = env("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (env("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        foreach (var raw in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim();
            if (dir.Length == 0) continue;
            foreach (var candidate in Candidates(dir, command, extensions))
                if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string dir, string command, string[] extensions)
    {
        yield return Path.Combine(dir, command);
        foreach (var ext in extensions)
            yield return Path.Combine(dir, command + (ext.StartsWith('.') ? ext : "." + ext));
    }
}
