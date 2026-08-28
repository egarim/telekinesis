# Remote deployment — transport, safety gate, audit

Telekinesis is total-machine-control tooling; its remote story is deliberately
conservative.

## Transports

- **stdio (default)** — `telekinesis`. The MCP client owns the process; nothing
  listens on the network. This is the recommended mode everywhere it fits.
- **HTTP/SSE** — `telekinesis serve --sse --port 3001`. Binds **127.0.0.1 only**.
  For anything beyond the local machine, put it behind an *authenticated tunnel*
  (the Lun.Os tunnel, an SSH -L forward, or a reverse proxy that terminates auth).
  Never expose the port itself; there is intentionally no listen-on-0.0.0.0 flag.

## The safety gate

Remote posture defaults to perception:

| Mode | Perception tools | Action tools |
|---|---|---|
| `telekinesis` (stdio) | yes | yes (unless `--read-only`) |
| `telekinesis --read-only` | yes | no |
| `telekinesis serve --sse` | yes | **no — read-only by default** |
| `telekinesis serve --sse --enable-actions` | yes | yes |

`--read-only` always wins. The `assert_element` tool is classified as perception
(it only polls the tree) and is available in every mode.

## Audit log

Every action tool call (including refusals-worthy failures) is appended as one
JSON line to:

- `$XDG_STATE_HOME/telekinesis/audit.log` when set,
- otherwise `~/.local/state/telekinesis/audit.log` (Linux/macOS)
  or `%LOCALAPPDATA%\Telekinesis\state\telekinesis\audit.log` (Windows).

Fields: timestamp, tool, target, success, action path. Secrets never appear —
see below. The stderr line MCP clients already show is unchanged.

## Credentials — the handoff rule

`fill_credential(elementId, applicationId, field)` fills a password/username
field **without the secret ever passing through the model, the server, or the
log**. It focuses the field, then invokes the host credential provider
configured in `TELEKINESIS_CREDENTIAL_CMD` — typically a password manager's
auto-type command (KeePassXC auto-type, `op` + a typing bridge, etc.). The
provider types the value itself; Telekinesis passes only metadata
(`TK_CRED_FIELD`, `TK_CRED_APP`, `TK_CRED_ELEMENT` env vars) and reports
success/failure.

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
