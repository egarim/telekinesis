# Command reference

Every subcommand and every flag `telekinesis` accepts. The one-shot verbs have
their own guide in [HEADLESS-CLI.md](HEADLESS-CLI.md) — this page is the
complete surface, including the developer-facing commands that only ever
appeared as scattered examples before.

> There is currently **no `--help` and no `--version`**. `telekinesis --help`
> falls through to the stdio MCP server and waits on stdin, which looks like a
> hang. Use this page instead
> ([#55](https://github.com/egarim/telekinesis/issues/55) tracks adding them).

## How arguments are dispatched

Order matters, and it is not a conventional parser:

1. A **one-shot verb** as the *first* argument wins
   (`apps tree find read focused snapshot click click-at invoke set-text type press launch`).
   Matching the first argument only means an operand that happens to read
   `probe` is never hijacked.
2. Then `probe` and `repl` are matched **anywhere in the argument list**
   (`args.Contains`), so `telekinesis --enable-actions probe` works.
3. Then `run`, `pilot`, `pilot-eval`, `assert` and `serve` are matched as the
   *first* argument only.
4. Then `doctor`, `memory` and `setup` are matched **anywhere**.
5. Anything left is the **stdio MCP server**.

Because of step 4, a stray `doctor` or `setup` token anywhere on the line
diverts the whole command. Keep subcommands first.

| Exit code | Meaning |
|---|---|
| `0` | success (or, for `assert`, the condition held) |
| `1` | failed — not ready, no match, connect error |
| `2` | usage error, or an action refused for want of `--enable-actions` |

## The safety gate

`--enable-actions` is required by every path that can change the machine, and it
is checked per-invocation. The one exception is the **stdio MCP server**, which
is full-power by default; `--read-only` is how you take actions away there. See
[REMOTE.md](REMOTE.md#the-safety-gate).

Because stdio defaults dangerous, a near-miss of `--read-only` (`--readonly`,
`--read_only`, `--read.only`, `-read-only`, `/read-only`, `--READ-ONLY`,
`--read-only=false`, or an em-dashed paste) makes the server **refuse to start**
with exit 2 rather than silently run with actions enabled
([#52](https://github.com/egarim/telekinesis/issues/52)). Unrelated arguments —
including the .NET host's own `--environment` and `--Logging:*` — pass through
untouched.

---

## `telekinesis` — stdio MCP server

```
telekinesis [--read-only]
```

The default. Speaks MCP over stdin/stdout; nothing listens on the network. With
`--read-only`, action tools are not registered at all.

## One-shot verbs

```
telekinesis apps | tree | find | read | focused | snapshot        # perception
telekinesis launch | click | click-at | invoke | set-text | type | press
```

Full guide: [HEADLESS-CLI.md](HEADLESS-CLI.md). Two details that live only here:

- **`--` ends flag parsing.** Everything after it is a verbatim operand. Use it
  when an operand would otherwise look like a flag.
- **`launch` forwards everything verbatim** (minus `--enable-actions`) so the
  launched program's own flags survive. Other verbs strip the known
  value-taking flags `--app --depth --action --button --scope` and their values.

## `telekinesis probe` — exercise the backend from a terminal

The backend-validation and X-ray tool. Connects, runs one thing, exits. No MCP
client involved.

```
telekinesis probe [--app <id>] [--find "<name>"] [--depth N] [flags]
```

| Flag | Effect |
|---|---|
| *(none)* | list applications |
| `--app <id>` | scope to one application (`pid:1234`) |
| `--depth N` | tree depth (default **2**) |
| `--find "<name>"` | semantic search |
| `--click "<name>"` | native invoke — needs `--enable-actions` |
| `--set-text "<text>"` | set a field's text and read it back — needs `--enable-actions` |
| `--set-value <number>` | set a range/slider value; **requires `--find`** to pick the target |
| `--type "<text>"` | type into the focused element — needs `--enable-actions` |
| `--keys "ctrl+s"` | send a key chord — needs `--enable-actions` |
| `--click-at "x,y"` | click absolute screen coordinates — needs `--enable-actions` |
| `--screenshot <file>` | capture the screen (or `--region`) to a PNG |
| `--parse` | run the screen through the OmniParser sidecar and print elements ([VISION.md](VISION.md)) |
| `--region "x,y,w,h"` | restrict `--screenshot` / `--parse` to a rectangle |
| `--overlay` | draw live labeled boxes over an app; **requires `--app`** ([XRAY-OVERLAY.md](XRAY-OVERLAY.md)) |
| `--for N` | seconds to keep `--overlay` up |
| `--recall` | show what perceptual memory has learned; **requires `--app`** |
| `--show` | with `--recall`, render the remembered targets |
| `--enable-actions` | required by every acting flag above |

Precedence when several are given: `--overlay`, then `--recall`, then
`--screenshot`, then `--parse`, then `--click-at`, then the tree/find/action
dispatch.

## `telekinesis repl` — persistent interactive session

```
telekinesis repl [--enable-actions]
```

Connects once, then reads commands from stdin and prints each command's elapsed
time. This is the honest way to measure action latency: a one-shot `probe` pays
process start, JIT and backend connect on every call, which drowns the
tens-of-milliseconds action itself.

| Command | Notes |
|---|---|
| `apps` | |
| `tree <app> [depth]` | depth default 3 |
| `find <app> <name>` | |
| `click <app> <name>` | needs `--enable-actions` |
| `expand <app> <name>` / `collapse` / `toggle` | needs `--enable-actions`; **not listed in the REPL's own banner** |
| `settext <app> <name> <text>` | needs `--enable-actions` |
| `setvalue <app> <name> <num>` | needs `--enable-actions` |
| `con-open [shell]` | console tier — see [CONSOLE.md](CONSOLE.md) |
| `con-write <id> <text>` | |
| `con-read <id>` | |
| `con-close <id>` | |
| `quit` / `exit` | |

Arguments are tokenized with quote support, so names with spaces work.

## `telekinesis run` — scripted scenarios

```
telekinesis run <scenario.json> [--enable-actions]
```

Executes a scripted, self-verifying demo with caption output and exits nonzero
on the first failure. File format and the shipped scenarios:
[SCENARIOS.md](SCENARIOS.md).

## `telekinesis assert` — boolean probe for CI

```
telekinesis assert [--role <Role>] [--name <name>] [--app <id>]
                   [--must-be visible|enabled|…] [--timeout-ms N]
```

Exit `0` when a matching element is found within the timeout, `1` otherwise.
`--timeout-ms` defaults to **3000**. Perception only — it never needs
`--enable-actions`, and it is available in every mode.

On Windows it relays into the console session exactly like the one-shot verbs
(see [HEADLESS-CLI.md](HEADLESS-CLI.md#the-windows-session-trap-and-the-relay)).

## `telekinesis pilot` — the local-model UI brain

```
telekinesis pilot "<goal>" --app pid:N [--max-steps N] [--model name]
                  [--brain-url url] [--dry-run] --enable-actions
```

A small local model plans one schema-constrained action per step over a compact
candidate list; every step is trace-logged. `--max-steps` defaults to **12**.
`--dry-run` plans without executing and is the only way to run it without
`--enable-actions`. Needs a reachable Ollama-compatible brain. Details:
[PILOT.md](PILOT.md).

```
telekinesis pilot-eval <trace.jsonl> [--model name] [--brain-url url]
```

Replays a recorded trace through a brain without touching the UI and reports
agreement rate and latency (median, p95) — how you compare models without a
desktop.

## `telekinesis serve` — MCP over HTTP/SSE

```
telekinesis serve [--port N] [--enable-actions]
```

Binds **127.0.0.1 only**; there is intentionally no listen-on-all-interfaces
flag. Read-only unless `--enable-actions`. `--port` defaults to **3001**.

Two things worth knowing:

- **`--sse` is not a real flag.** Older examples write `serve --sse`; the code
  never looks for it, so plain `telekinesis serve` is the HTTP/SSE server and
  the `--sse` token is simply ignored. It is harmless, not required.
- **`--read-only` is not consulted here.** `serve` decides purely on
  `--enable-actions`, so `serve --read-only --enable-actions` serves *actions*.
  The default is already read-only; do not rely on `--read-only` to make it so.

Deployment posture: [REMOTE.md](REMOTE.md).

## `telekinesis doctor` — diagnose the environment

```
telekinesis doctor
```

Prints one line per check and exits `0` when ready, `1` otherwise. Reports the
backend and its checks, then — as advisory lines that never block readiness —
the OmniParser vision sidecar, the CDP tier and what answers at its endpoint,
every loaded provider plugin (external ones flagged `[!!]` because they run with
the server's full power), and each running browser with whether its page tree is
realized.

## `telekinesis memory` — perceptual memory

```
telekinesis memory                          # stats: cached parses, anchors, directory
telekinesis memory export --out <dir>       # dump as a training-ready dataset
```

`export` writes `dataset.jsonl` plus a `crops/` directory. See
[PERCEPTUAL-MEMORY.md](PERCEPTUAL-MEMORY.md).

## `telekinesis setup` — print platform setup steps

```
telekinesis setup
```

Prints the Linux udev rule for `/dev/uinput`, the accessibility-bus gsettings
command, and the macOS Accessibility permission step. Informational only — it
changes nothing.

## Environment variables

| Variable | Read by | Effect |
|---|---|---|
| `TELEKINESIS_CDP` | CDP tier | `1` enables the browser DevTools tier ([BROWSERS.md](BROWSERS.md)) |
| `TELEKINESIS_CDP_PORT` | CDP tier | debugging port to attach to (default `9222`) |
| `TELEKINESIS_CONPTY` | console tier | `1` force-enables the experimental Windows ConPTY path ([CONSOLE.md](CONSOLE.md)) |
| `TELEKINESIS_CREDENTIAL_CMD` | `fill_credential` | host command that types the secret ([REMOTE.md](REMOTE.md#credentials--the-handoff-rule)) |
| `TELEKINESIS_SHOW_INTENT` | actions | `1` flashes each injected action's target before the input lands ([XRAY-OVERLAY.md](XRAY-OVERLAY.md)) |
| `TELEKINESIS_NO_RELAY` | Windows relay | `1` disables the console-session relay |
| `TELEKINESIS_RELAY_TIMEOUT` | Windows relay | relay timeout in seconds (default `60`) |
| `TELEKINESIS_OMNIPARSER_URL` | vision tier | OmniParser sidecar base URL ([VISION.md](VISION.md)) |
| `TELEKINESIS_LEARN` | perceptual memory | `1`/`true` records a perceptual anchor per action; opt-in because it costs a region capture each time ([PERCEPTUAL-MEMORY.md](PERCEPTUAL-MEMORY.md)) |
| `TELEKINESIS_MEMORY_DIR` | perceptual memory | overrides the store (default `%LOCALAPPDATA%/Telekinesis/perceptual-memory`) |
| `TELEKINESIS_BRAIN_URL` | `pilot` | Ollama-compatible endpoint ([PILOT.md](PILOT.md)) |
| `TELEKINESIS_BRAIN_MODEL` | `pilot` | default model name |
| `XDG_STATE_HOME` | audit log | overrides where `telekinesis/audit.log` is written |
| `SHELL` | `console_open` | the default shell on Linux/macOS (falls back to `/bin/sh`) |

That is the complete set — every `GetEnvironmentVariable` call in the tree.

`TK_CRED_FIELD`, `TK_CRED_APP` and `TK_CRED_ELEMENT` are set *by* Telekinesis
for the credential provider it invokes — they are outputs, not settings.
