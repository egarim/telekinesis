# Remote deployment — transport, safety gate, audit

Telekinesis is total-machine-control tooling; its remote story is deliberately
conservative.

## Transports

- **stdio (default)** — `telekinesis`. The MCP client owns the process; nothing
  listens on the network. This is the recommended mode everywhere it fits.
- **HTTP/SSE** — `telekinesis serve --port 3001` (port defaults to 3001). Binds
  **127.0.0.1 only**. Older examples write `serve --sse`; `--sse` was never a
  real flag — the code does not look for it — so it is accepted and ignored, and
  plain `serve` is the HTTP/SSE server.

  Stated plainly, because the rest of this page depends on it: **the endpoint has
  no authentication and no TLS.** Anything that can reach the port can call the
  tools that are registered on it. Loopback binding is the whole access control,
  which is why the tunnel below is not optional.
  For anything beyond the local machine, put it behind an *authenticated tunnel*
  (the Lun.Os tunnel, an SSH -L forward, or a reverse proxy that terminates auth).
  Never expose the port itself; there is intentionally no listen-on-0.0.0.0 flag.

## The safety gate

Remote posture defaults to perception:

| Mode | Perception tools | Action tools |
|---|---|---|
| `telekinesis` (stdio) | yes | yes (unless `--read-only`) |
| `telekinesis --read-only` | yes | no |
| `telekinesis serve` | yes | **no — read-only by default** |
| `telekinesis serve --enable-actions` | yes | yes |

**`--read-only` applies to the stdio server only.** That is the one transport
that is full-power by default, so it is the one that needs taking power away.
`serve` decides purely on `--enable-actions` and never reads `--read-only` — so
`serve --read-only --enable-actions` serves *actions*. Do not use `--read-only`
to make `serve` safe; it already is, unless you ask otherwise.

Because stdio defaults dangerous, it **refuses to start on a near-miss of that
flag** rather than ignoring it (issue #52) — `--readonly`, `--read_only`,
`--read.only`, `-read-only`, `/read-only`, `--READ-ONLY`, `--read-only=false`
and an em-dashed `—read-only` paste all exit 2 with a hint. Silently dropping
such a typo would start the server in full action mode, exactly backwards from
the operator's intent. Unrelated arguments (including the .NET host's own
`--environment` / `--Logging:*` config args) pass through untouched.

The `assert_element` tool is classified as perception (it only polls the tree)
and is available in every mode.

The optional CDP browser tier follows the same gate: its read tools
(`browser_targets`, `browser_console`, `browser_network`) are perception, while
`browser_evaluate` is an action — absent under `--read-only` and over
`serve` without `--enable-actions`. The tier's own rules (opt-in,
loopback-only, no headers or bodies) live in
[docs/BROWSERS.md](BROWSERS.md#the-cdp-tier--console-network-and-js).

## Audit log

Every action tool call (including refusals-worthy failures) is appended as one
JSON line to:

- `$XDG_STATE_HOME/telekinesis/audit.log` when set,
- otherwise `~/.local/state/telekinesis/audit.log` (Linux/macOS)
  or `%LOCALAPPDATA%\Telekinesis\state\telekinesis\audit.log` (Windows).

Fields: timestamp, tool, target, success, action path. `fill_credential` logs
only field metadata, never the secret.

**One tool does write what you gave it:** `console_write` records the typed text
verbatim, because a shell command *is* the audit trail. So the log is only as
clean as what you type into a PTY — never type a credential into a console
session ([CONSOLE.md](CONSOLE.md#security)).

One tool also echoes a line to **stderr**, separate from the audit file:

```
[telekinesis] <timestamp> browser_evaluate target=<url> success=<bool>
```

That matters when stderr is captured into a shared log — the audit file is not
the only place a `browser_evaluate` call is recorded. The URL is projected, but
it is the URL captured when the session attached, which may not be the page the
expression ran against; see
[BROWSERS.md](BROWSERS.md#parameters-worth-knowing).

## Credentials — the handoff rule

`fill_credential(elementId, applicationId, field)` fills a password/username
field **without the secret ever passing through the model, the server, or the
log**. It focuses the field, then invokes the host credential provider
configured in `TELEKINESIS_CREDENTIAL_CMD` — typically a password manager's
auto-type command (KeePassXC auto-type, `op` + a typing bridge, etc.). The
provider types the value itself; Telekinesis passes only metadata, as environment
variables on the provider process, and reports success/failure.

Those three variables are the whole contract with the command you write, so their
values matter:

| Variable | Value |
|---|---|
| `TK_CRED_FIELD` | the requested field string — `password`, `username`, `totp`, … (whatever the caller asked for) |
| `TK_CRED_APP` | the backend's **application id** — and its format is platform-specific: `pid:N` on Windows, the bare pid (`4812`) on macOS, an AT-SPI D-Bus bus name (`:1.234`, no pid in it at all) on Linux |
| `TK_CRED_ELEMENT` | the target field's accessible Name, or an empty string when it has none |

The middle row is the one that surprises people. A wrapper that tries to match
vault entries on an application *name* gets an id instead and matches nothing —
and the id is not even the same shape across platforms, so parse it per platform
rather than assuming `pid:`. On Linux there is no pid in it to recover; resolve
the bus name if you need the process.

- No provider configured → the tool returns `available: false` with setup
  guidance. It **never** falls back to typing a secret from model context.
- `read_element` on a `Protected` field keeps returning masked (null) text in
  every mode — verified by the password-field tests in
  docs/RUNNING-ON-WINDOWS.md §5.

## Scenario runner and CI

- `telekinesis run demos/<scenario>.json --enable-actions` executes a scripted
  demo with caption output and exits nonzero on the first failure — see
  `demos/calc-add.json` (Windows-validated) and `demos/thunar-navigate.json`
  (Linux-validated).
- `telekinesis assert --role Button --name Save --must-be visible --timeout-ms 5000`
  is the shell-friendly probe for CI: exit 0 when the condition holds, 1 when not.
