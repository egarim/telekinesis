# Running Telekinesis on macOS

The macOS backend is an Accessibility API (`AXUIElement`) client. It mirrors the Linux
backend's shape: native actions first (`AXPress` / `AXSetAttributeValue`), CGEvent
injection as the fallback.

## Grant Accessibility permission first (required)
Every AX read/write silently fails until the process holding the session has macOS
**Accessibility** permission. `telekinesis doctor` reports this.

1. Open **System Settings → Privacy & Security → Accessibility**.
2. Enable the app that launches Telekinesis — your **terminal** (Terminal.app / iTerm),
   or the **Claude** app if you run it from there, or the packaged binary itself.
3. Re-run `telekinesis doctor` — it should show `accessibility-permission: granted`.

Gotcha: the grant is tied to the binary's **code signature**. A freshly rebuilt, unsigned
`dotnet` binary loses trust, which is why granting the *terminal* is the most stable choice
during development. The same grant covers both reading (AXAPI) and input injection (CGEvent).

## Validation
`telekinesis probe` works the same as on Linux:
```
telekinesis doctor
telekinesis probe                       # list applications (CGWindowList — works without AX trust)
telekinesis probe --app <pid> --depth 3 # walk a tree (needs AX trust)
telekinesis probe --find "Save"
telekinesis probe --enable-actions --click "Save"                 # AXPress, else CGEvent click
telekinesis probe --enable-actions --app <pid> --set-text "hello" # AXSetValue + read-back
```
Application ids are the process id (from `probe`).

## Status
- **Validated without permission:** `list_applications` (CGWindowList + all CoreFoundation
  marshalling), and graceful degradation of AX calls when untrusted (no crash).
- **Pending an Accessibility grant to validate live:** tree walk, find, native invoke/set_text,
  focus events, CGEvent injection. All are implemented against real apps (TextEdit, Finder,
  Calculator) — grant permission and run the commands above to exercise them.

## Notes / future work
- `wait_for` uses focus polling; an `AXObserver` run-loop is a later optimization.
- Element handles are retained AX refs in an in-process table; the long-lived MCP server
  should cap/evict it (fine for `probe`, which is per-invocation).
