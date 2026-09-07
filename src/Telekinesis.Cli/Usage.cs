using System.Reflection;

namespace Telekinesis.Cli;

/// <summary>
/// `telekinesis --help` and `--version` (issue #55). Deliberately a summary, not a
/// manual: the exhaustive flag-by-flag reference is docs/CLI.md, and duplicating it
/// here would guarantee the two drift. What belongs here is the shape of the tool —
/// which subcommands exist, what the default does, and where the safety gate is —
/// plus the pointer.
/// </summary>
internal static class Usage
{
    /// <summary>Informational version without the `+commit` build metadata.</summary>
    public static string Version
    {
        get
        {
            var v = typeof(Usage).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Usage).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            var plus = v.IndexOf('+');
            return $"telekinesis {(plus > 0 ? v[..plus] : v)}";
        }
    }

    public const string Text = """
        telekinesis — drive the desktop through the accessibility tree.

        USAGE
          telekinesis [--read-only]              MCP server on stdio (the default)
          telekinesis <verb> [args]              one-shot: JSON to stdout, then exit
          telekinesis <subcommand> [flags]

        The default, with no arguments, is the stdio MCP server. It has actions
        ENABLED; --read-only takes them away. Every other path that can change the
        machine requires --enable-actions on that invocation.

        ONE-SHOT VERBS                           (docs/HEADLESS-CLI.md)
          apps tree find read focused snapshot   perception, always available
          launch click click-at invoke           actions, need --enable-actions
          set-text type press

        SUBCOMMANDS                              (docs/CLI.md)
          serve [--port N] [--enable-actions]    MCP over HTTP, 127.0.0.1 only
          run <scenario.json>                    scripted, self-verifying demo
          assert [--role R] [--name N]           0/1 exit probe for CI
          probe [flags]                          exercise the backend from a terminal
          repl                                   persistent session, timed commands
          pilot "<goal>" --app pid:N             local-model UI brain
          pilot-eval <trace.jsonl>               replay a trace through a brain
          doctor                                 diagnose this machine
          setup                                  print the platform setup steps
          memory [export --out <dir>]            perceptual-memory stats / dataset

        EXIT CODES
          0 ok    1 failed    2 usage error, or an action refused without
                              --enable-actions

        Every subcommand, every flag, every environment variable:
          https://github.com/egarim/telekinesis/blob/main/docs/CLI.md
        """;
}
