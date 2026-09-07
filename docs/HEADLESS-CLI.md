# Headless one-shot CLI

Drive Telekinesis from any shell — SSH, cron, CI, a shell-only agent — with **no MCP
client and no session**. Every verb is one process: it prints JSON to stdout and exits
(`0` ok, `1` failed, `2` usage error / action refused). Born from
[#37](https://github.com/egarim/telekinesis/issues/37): an agent with only SSH on a
remote Windows box needs to launch a GUI app and click through it.

## Verbs

Perception (always available):

```
telekinesis apps                          # list applications (id, name, pid)
telekinesis tree [--app X] [--depth N]    # accessibility tree (default: focused app, depth 3)
telekinesis find "<query>" [--app X] [--scope window|page|chrome]
telekinesis read "<query>"  [--app X]     # full detail of the best match
telekinesis focused                       # currently focused element
telekinesis snapshot [--app X]            # one-call map of actionable elements
```

Actions (each invocation needs `--enable-actions`, mirroring `serve`'s posture —
default is perception-only):

```
telekinesis launch <exe> [args…]          # start a GUI app on the INTERACTIVE desktop
telekinesis click "<query>"   [--app X] [--button left|middle|right]
telekinesis invoke "<query>"  [--app X] [--action invoke|expand|collapse|toggle|select]
telekinesis set-text "<query>" "<text>" [--app X]
telekinesis click-at <x> <y>
telekinesis type "<text>"                 # into the focused element
telekinesis press "<keys>"                # e.g. "ctrl+s", "enter"
```

`telekinesis assert` is the matching boolean probe — use it after an action to wait for
the effect:

```
telekinesis assert [--role <Role>] [--name <name>] [--app X]
                   [--must-be visible|enabled|…] [--timeout-ms N]   # default 3000
```

Exit `0` when a match appears within the timeout, `1` otherwise. It is perception, so
it never needs `--enable-actions` and works in every mode — including against a
`--read-only` server.

### Flags and operands

The value-taking flags are `--app`, `--depth`, `--action`, `--button` and `--scope`.
Everything that is not a flag or a flag's value is a positional operand, in order — so
`set-text "<query>" "<text>"` is two operands.

Two rules worth knowing:

- **`--` means "verbatim from here".** Everything after it is an operand even if it
  starts with `--`. Use it whenever an operand could be mistaken for a flag:
  `telekinesis find -- "--verbose"`.
- **`launch` forwards everything verbatim** (except `--enable-actions`), because the
  program you are launching has its own flags:
  `telekinesis launch myapp.exe --port 8080 --enable-actions` passes `--port 8080` to
  `myapp.exe`, not to Telekinesis.

The complete reference for every subcommand — including `probe`, `repl` and `pilot`,
which are not one-shot verbs — is [CLI.md](CLI.md).

## Query addressing

Element ids can't survive across separate processes, so targets are **queries,
re-resolved on every call**:

- `"Save"` — case-insensitive name substring, any role
- `"Button:Save"` — role-qualified (`Role:name`); any `AccessibleRole` works
  (`Edit:Enrollment code`, `MenuItem:File`, …)

Resolution prefers an exact name match, then a visible+enabled match, then the first
hit. `snapshot` emits a ready-made `query` per element, so the loop is:
`snapshot` → pick a `query` → act with it.

## The Windows session trap, and the relay

A process started over SSH on Windows runs in **session 0**, not the interactive
console session. Live-proven on real hardware: from there UIA sees *no windows at
all* (`apps` returns `[]`), input injection reaches no desktop, and a launched GUI
app never renders. So on Windows the CLI handles both directions itself:

- **`launch`** routes through a one-shot Scheduled Task (`/IT`), which the Task
  Scheduler starts **in the logged-on user's console session**. No extra privileges
  needed when the SSH user is the logged-on user.
- **Every other verb auto-relays**: when the CLI detects it is outside the console
  session, it re-runs the same command line there via a hidden one-shot Scheduled
  Task (no window flashes on the user's desktop) and streams back stdout, stderr,
  and the exit code. `ssh winbox telekinesis apps` just works; a `[telekinesis]
  … relaying …` note goes to stderr. A user must be logged on at the console;
  the relay times out after 60 s (`TELEKINESIS_RELAY_TIMEOUT` overrides). Set
  `TELEKINESIS_NO_RELAY=1` to disable.

On Linux/macOS, `launch` is a plain child-process start — the CLI exits
immediately, so the app is reparented and lives on (point `DISPLAY` at the desktop
as usual); perception/actions need no relay.

A launched process is started by the Task Scheduler, so no pid is returned — verify
with:

```
telekinesis assert --name "My App" --timeout-ms 10000
```

UWP apps (Calculator, Media Player, …) all host their windows in one
`ApplicationFrameHost.exe`, so they share one `pid:N` application id — scope queries
by role/name, not by app alone.

## Example: remote verification over SSH

```bash
ssh winbox telekinesis launch 'C:\apps\collector.exe' --enable-actions
ssh winbox telekinesis assert --name "Collector" --timeout-ms 15000
ssh winbox telekinesis apps          # → find the app's id, e.g. pid:4242
ssh winbox telekinesis snapshot --app pid:4242
ssh winbox telekinesis set-text "Edit:Enrollment code" "ABC-123" --app pid:4242 --enable-actions
ssh winbox telekinesis click "Button:Save" --app pid:4242 --enable-actions
ssh winbox telekinesis assert --name "Saved" --must-be visible
```

All actions land in the same audit log as the MCP tools.
