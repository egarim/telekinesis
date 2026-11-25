# Interactive terminals — the console tier

Telekinesis drives GUIs through the accessibility tree. Some of what an agent
needs to do has no GUI at all: run a build, tail a log, answer a prompt from an
installer, drive a TUI. Shelling out per command loses everything that makes a
terminal a terminal — the working directory, the environment, an ssh session, a
REPL that is mid-expression, a program waiting on stdin.

The console tier (issue #27) gives the agent a **real PTY** instead: a
persistent terminal session it can type into and read back, where the read-back
is the *rendered screen* — what a human sitting at that terminal would see, with
ANSI escape sequences already applied rather than dumped as bytes.

## Platform support

| Platform | Backing | Status |
|---|---|---|
| Linux | `openpty` (`UnixPtyConsoleSession`) | supported |
| macOS | `openpty` (`UnixPtyConsoleSession`) | supported |
| Windows | ConPTY (`ConPtyConsoleSession`) | **off by default** — see below |
| any | Sentiora Terminal, out of process (`SentioraConsoleSession`) | **off by default** — opt-in alternative, see below |

On Windows `console_open` refuses with a `PlatformNotSupportedException` unless
`TELEKINESIS_CONPTY=1` is set. The ConPTY implementation ships and is the
canonical Windows path, but on Windows 11 ARM64 (build 26200) the child process
does not bind to the pseudoconsole and renders to the *parent* console
instead — which writes junk into the MCP stdio stream and breaks the session
that is driving it. That is [issue #46](https://github.com/egarim/telekinesis/issues/46)
and its "Windows ConPTY child-attach" follow-up,
[#49](https://github.com/egarim/telekinesis/issues/49).

```
TELEKINESIS_CONPTY=1 telekinesis        # opt into the experimental Windows path
```

Set it only if you are testing on a Windows build where child-attach works, and
never over `serve` where a corrupted stream is harder to notice.

On Windows the tier also needs the `net10.0-windows` target — a portable build
throws `"This build does not include ConPTY"`.

## Optional backend: Sentiora Terminal

[Sentiora Terminal](https://github.com/egarim/sentiora-terminal) is a Node.js process
that hosts PTY sessions behind a terminal emulator and exposes the *rendered screen*
over newline-delimited JSON-RPC on stdio. Telekinesis can use it as an alternative
console backend (issue #67), which matters mostly on Windows: it is an out-of-process
PTY, so the #46/#49 child-attach failure cannot corrupt the MCP stream even in
principle — the child's stdout is a different pipe in a different process.

It is **opt-in and off by default**. Nothing changes unless you set the switch, and
`ConPtyConsoleSession` / `UnixPtyConsoleSession` are untouched — #67 is explicit that
the native implementation stays until parity is demonstrated, so the two backends
remain experimentally comparable.

```
TELEKINESIS_SENTIORA=1                     # use the Sentiora backend
TELEKINESIS_SENTIORA_CMD="node /path/to/sentiora-terminal/src/server.js"
```

Runtime discovery, in order:

| Variable | Meaning |
|---|---|
| `TELEKINESIS_SENTIORA_CMD` | an explicit command line, e.g. `node /path/src/server.js` |
| `TELEKINESIS_SENTIORA_SERVER` | a path to `server.js`, run under Node |
| `TELEKINESIS_SENTIORA_NODE` | the Node executable for the above (default `node`) |
| *(none of the above)* | `sentiora-terminal` on `PATH` |

If the backend is requested but cannot be found or started, Telekinesis **says so on
stderr and falls back to the native backend** — it never degrades silently, because
then you could not tell which backend produced a result. On success it announces
itself once per host process, with the handshake details, so a run is attributable:

```
[telekinesis] console backend: Sentiora Terminal (protocol v1, platform=win32, arch=arm64, conptyDll=false); runtime: node /path/src/server.js (from TELEKINESIS_SENTIORA_CMD)
```

The `protocolVersion` is checked at handshake and a mismatch is refused rather than
attempted.

### This backend's read-back is not a byte stream

`IConsoleSession` is documented as pushing *raw output bytes* to a callback from a
background reader. **Sentiora never exposes raw bytes** — it exposes the rendered
screen after its emulator applied the escape sequences, which is the point of its
design. So the adapter synthesises an approximation: a background poll loop diffs
consecutive observations and pushes `ESC[2J ESC[H` followed by the whole visible grid.

The screen you read back is right. The *stream* is not, and cannot be made so:

- content that scrolled off Sentiora's viewport is unrecoverable — an observation
  carries only the visible rows, never scrollback;
- byte-level framing is gone; the child's writes are coalesced into whatever the poll
  interval caught;
- escape sequences with no visible effect (SGR colour, OSC titles) are gone — the
  screen is already colour-stripped;
- granularity is the ~250 ms poll timeout, not the child's write granularity.

Use `console_read`, which was always "a snapshot, not a stream". Do not treat this
backend's output callback as a faithful record of what the child printed.

### Limits that differ from the native backends

All measured on Windows 11 ARM64; the adapter is built around them rather than
assuming them away.

| Constraint | Value | What the adapter does |
|---|---|---|
| Terminal size | cols **2–300**, rows **2–150** (vs Telekinesis's 2–1000) | clamps before the call; `console_open`/`console_resize` report the size **actually applied**, and the screen grid is resized to match |
| Concurrent sessions | **16** per host process | starts another host rather than failing |
| Host lifetime | degrades from ~**363** create/close cycles, hard failure near ~**495** (`Invalid pty handle`) | retires a host at **360** creates and starts a replacement; a PTY cannot migrate, so the old host stays until its own sessions drain, then is reaped |
| Exit detection | ~**1025 ms** late | `alive` is *not* immediate — poll `console_read` rather than trusting `alive` the instant a command should have finished |
| Host shutdown | ~**5.1 s** after stdin closes | allows 6 s before force-killing the process tree |
| Cancellation | **none** — an in-flight wait completes or times out | polls with 250 ms timeouts, so that is also the cancellation granularity |
| Input size | ≤ 65536 chars per write | chunks longer writes (chunk boundaries are *not* child-visible boundaries; ConPTY coalesces and splits on its own) |

Two protocol details the adapter hides, because `console_write` has no concept of
either: every write needs a **fresh `revision`** or it is rejected as
`Stale observation` (so writes re-observe and retry, bounded at 5), and every mutating
call needs the **`owner` token** returned at create. Writes are serialised per session.

Ctrl-C is unchanged at the tool level — send `"\u0003"`. But note that Ctrl-C sent
*before the child enables raw mode* terminates it with `0xC000013A`
(`STATUS_CONTROL_C_EXIT`) rather than interrupting it, and once the child is in raw
mode the same byte arrives as data, not as a signal. Wait for an observable readiness
marker (a prompt on screen) before interrupting.

### Testing this backend

`src/Telekinesis.Cli.Tests` has two clearly separated halves:

- **Stubbed** (`FakeSentioraTransport`): framing, revision/ownership retry, id mapping,
  clamping, error mapping, host recycling, discovery and fallback. Deterministic, and
  needs no Node.js in CI. Proves the adapter speaks the protocol — **not** that the
  real runtime agrees.
- **Real runtime** (`SentioraRealRuntimeTests`): spawns an actual Sentiora host. These
  are conditional and **return without asserting when no runtime is discoverable**, so
  a green CI run does not mean the integration was verified. To actually run them:

  ```
  TELEKINESIS_SENTIORA_CMD="node /path/to/sentiora-terminal/src/server.js" \
    dotnet test src/Telekinesis.Cli.Tests --filter "FullyQualifiedName~SentioraRealRuntimeTests" \
    --logger "console;verbosity=detailed"
  ```

### Licensing and dependency boundary

The coupling is a JSON-RPC contract over stdio plus a discovered executable. There is
**no build-time dependency** from Telekinesis on Sentiora — no package reference, no
vendored code — and Telekinesis builds and runs normally with Sentiora entirely absent.
Sentiora likewise gains nothing from Telekinesis. Each remains independently usable and
independently licensed.

## Tools

Every one of these is in the **action** tier: they are absent under
`--read-only` and over `serve` without `--enable-actions`. Typing into a
shell is arbitrary code execution, so it is gated exactly like `click` is, and
every mutating call is audit-logged.

| Tool | Parameters | Returns |
|---|---|---|
| `console_open` | `shell` (empty = `cmd.exe` on Windows, `$SHELL` else `/bin/sh`), `cols` (default 120), `rows` (default 30) | `{sessionId, shell, cols, rows, screen}` |
| `console_write` | `sessionId`, `text`, `sendEnter` (default **true**) | `{ok, alive}` |
| `console_read` | `sessionId`, `lines` (0 = whole screen) | `{screen, alive}` |
| `console_resize` | `sessionId`, `cols`, `rows` (clamped to 2–1000) | `{ok, cols, rows}` |
| `console_close` | `sessionId` | `{ok}` |
| `console_list` | — | `[{sessionId, shell, alive, opened}]` |

Session ids are `con1`, `con2`, … and are unique per server process.

### The read model

`console_read` is a **snapshot, not a stream.** It renders the current state of
the session's `cols`×`rows` screen buffer (120×30 by default), so:

- Output that scrolled off the top is gone. Ask for a larger `rows` at
  `console_open` if you need more scrollback in view, or read more often.
- Reading twice without writing returns the same screen. There is no cursor into
  "new output since last read" — compare screens yourself if you need a delta.
- A program that redraws in place (a progress bar, `top`, an installer TUI)
  reads correctly, which is the whole point of rendering rather than
  concatenating.

Sizes are clamped to **2–1000** in each direction, because the grid is allocated
up front and an unbounded size would be an out-of-memory switch
([#58](https://github.com/egarim/telekinesis/issues/58)). `console_open` and
`console_resize` both report the dimensions actually applied, so a request outside
the range tells you what you got rather than failing silently.

`console_open` waits 300 ms before returning so the shell has painted its
banner and prompt; `console_write` waits 250 ms so the program has a beat to
react before your next `console_read`. Neither is a guarantee — for anything
slower, poll `console_read` until the screen stops changing or the prompt
returns.

### Sending control characters

`console_write` sends literal text, and `sendEnter` appends the Enter key (a
carriage return). It **defaults to true**, so a plain `{sessionId, text}` call
submits the command — which is what you want for running something, and why the
default is that way round ([#57](https://github.com/egarim/telekinesis/issues/57)).

Pass `sendEnter: false` when you are answering a prompt that reads a single
keypress, or sending a control character:

| Key | `text` value |
|---|---|
| Ctrl-C (interrupt) | `"\u0003"` |
| Ctrl-D (EOF) | `"\u0004"` |
| Ctrl-Z (suspend) | `"\u001a"` |
| Escape (e.g. leaving vim insert mode) | `"\u001b"` |
| Tab (completion) | `"\t"` |

Arrow keys and function keys are the usual ANSI sequences — up is
`"\u001b[A"`, down `"\u001b[B"`, right `"\u001b[C"`, left `"\u001b[D"`.

### Lifetime

Sessions live until `console_close` or until the server process exits — they are
owned by a DI singleton, not by a request. An agent that opens sessions and
never closes them leaks child processes for the life of the server; call
`console_list` and clean up. Sessions do **not** survive a server restart, and
they are not shared between two clients connected to two different server
processes.

`alive: false` means the child process exited (the shell was closed, the command
finished). The session entry remains until you `console_close` it, so you can
still read the final screen.

## From the REPL

`telekinesis repl` exposes the same sessions for interactive debugging:

```
con-open [shell]      con-write <id> <text>      con-read <id>      con-close <id>
```

## Security

This is the sharpest tool in the box. `console_write` is unrestricted command
execution as whoever runs the server — there is no allowlist, no sandbox, and no
confirmation prompt. The protections are the ones that apply to every action:

- absent entirely under `--read-only`;
- absent over `serve` unless you passed `--enable-actions`;
- every `console_open`, `console_write`, `console_resize` and `console_close`
  appended to the audit log (see [REMOTE.md](REMOTE.md#audit-log)), with the
  written text recorded verbatim — so **do not type secrets into a console
  session**; use `fill_credential` for credentials.

Two asymmetries worth knowing:

- **`console_read` is not audited.** Writes leave a trail, reads do not — and the
  screen can hold anything the child painted: file contents, `env` output, a
  token echoed by a failing command. Nothing records that an agent read it.
- **The child inherits your whole environment.** On Unix every parent environment
  variable is copied into the PTY child, so any secret already in Telekinesis's
  environment is available to whatever runs in that session — and readable back
  off the screen if a command prints it. "Pass secrets via the environment" is
  not a mitigation here; the environment is already shared.

### Sentiora backend: secret-logging and environment audit

Required by #67. Findings, not mitigations — read them before enabling the backend
anywhere a credential is in scope.

**Environment inheritance is not controllable at the protocol level.** Sentiora spawns
every child with `env: process.env`, i.e. the *host process's* full environment, and
offers no way to pass a scoped one. The adapter starts that host with Telekinesis's own
environment, so anything in Telekinesis's environment is visible to every
Sentiora-hosted session. This is **not a regression**: `UnixPtyConsoleSession` copies
the whole parent environment into the PTY child, and `ConPtyConsoleSession` inherits it
by passing a null environment block. "Pass secrets via the environment" is not a
mitigation on any of these backends. The adapter deliberately does not scrub the host's
environment — stripping `PATH` or `HOME` would break Node resolution and the shell
inside the session. The only real mitigation is starting the Telekinesis process itself
with a minimal environment.

**Screens come back unredacted.** `screen` is returned verbatim, so a child that echoes
a password, token or key puts it into an observation the adapter holds and pushes into
the `TerminalScreen`. Note one correction to the analysis in Sentiora's proposed
integration contract: Telekinesis does **not** audit-log `console_read` results, because
`console_read` is not audited at all (see the asymmetry above). What the audit file does
record is `console_open` (the shell command line) and `console_write` (the typed text,
verbatim). So a credential printed *by the child* reaches the model and this process's
memory, but not the audit log; a credential typed *by the agent* reaches both. No
redaction exists anywhere in the chain — if you need it, it belongs in Telekinesis's
read/logging layer, not in the runtime.

**The `owner` token is a bearer credential and is never logged.** It is the only thing
standing between a caller and typing into a session. It is returned by
`terminal.create`, held only by the adapter, sent on every mutating call, and never
placed in an exception message, a diagnostic line or a tool reply. `SentioraAdapterTests`
pins that.

**Transport errors deliberately do not echo the offending line.** A line that fails to
parse cannot be inspected for secrets first, and the one response that carries a secret
— `terminal.create`'s, which returns the owner token — is exactly the kind of long line
a truncating diagnostic could cut mid-token. The message says *which* framing rule was
violated, never the payload that violated it.

**The host's stderr is captured, bounded, and never forwarded to stdout.** stdout is the
MCP stream; #46 is what happens when a child's output reaches it. Node and node-pty both
write benign-looking stack traces to stderr (e.g. `AttachConsole failed` from
node-pty's console-list helper), which are kept for diagnosis when a host actually
fails and are not treated as fatal.

Console output is untrusted, exactly like page content: a command's output can
contain text aimed at the agent reading it. Treat a rendered screen as data,
never as instructions.
