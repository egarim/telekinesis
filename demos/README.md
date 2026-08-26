# Telekinesis demo scenarios

Scripted flows for the demo reel (see `../docs/DEMO-PLAN.md`). Each `.json` is executed
by `telekinesis run <file>` (runner built by Codex, format in `../docs/CODEX-TASKS.md`).
The runner prints each `say` line as a caption while it drives the desktop, so the
recording narrates itself.

These target a **Linux desktop session** (the Lun.Os VM). Run `telekinesis doctor` first
to confirm the a11y bus and `/dev/uinput` are ready.

| File | Demo | Notes |
|---|---|---|
| `fill-out-contact.json` | 1 — fill a real GUI app | needs a form app open (GNOME Contacts) |
| `cross-app-copy.json` | 2 — read A, act in B | needs source + dest windows open |
| `survives-redesign.json` | 4 — semantic UI test | pair with a restyle step between runs |

Password-safety (demo 5) is shown via `read_element` on a login form plus the
`fill_credential` handoff — see the DEMO-PLAN; its scenario lands once Codex wires the tool.
