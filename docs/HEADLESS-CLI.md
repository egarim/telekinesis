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

`telekinesis assert` (see the README) is the matching boolean probe — use it after an
action to wait for the effect.

## Query addressing

Element ids can't survive across separate processes, so targets are **queries,
re-resolved on every call**:

- `"Save"` — case-insensitive name substring, any role
- `"Button:Save"` — role-qualified (`Role:name`); any `AccessibleRole` works
  (`Edit:Enrollment code`, `MenuItem:File`, …)

Resolution prefers an exact name match, then a visible+enabled match, then the first
hit. `snapshot` emits a ready-made `query` per element, so the loop is:
`snapshot` → pick a `query` → act with it.

## `launch` — the SSH story

A GUI app started from a Windows SSH session lands in a non-interactive session:
nothing renders and UIA sees nothing. `telekinesis launch` therefore routes through a
one-shot Scheduled Task on Windows, which the Task Scheduler starts **in the logged-on
user's console session**. No extra privileges needed when the SSH user is the
logged-on user. On Linux/macOS it is a plain detached start (point `DISPLAY` at the
desktop as usual).

The process is started by the Task Scheduler, so no pid is returned — verify with:

```
telekinesis assert --name "My App" --timeout-ms 10000
```

## Example: remote verification over SSH

```bash
ssh winbox telekinesis launch 'C:\apps\collector.exe' --enable-actions
ssh winbox telekinesis assert --name "Collector" --timeout-ms 15000
ssh winbox telekinesis snapshot --app pid:4242
ssh winbox telekinesis set-text "Edit:Enrollment code" "ABC-123" --app pid:4242 --enable-actions
ssh winbox telekinesis click "Button:Save" --app pid:4242 --enable-actions
ssh winbox telekinesis assert --name "Saved" --must-be visible
```

All actions land in the same audit log as the MCP tools.
