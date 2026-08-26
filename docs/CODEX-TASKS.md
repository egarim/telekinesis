# Codex task brief — Telekinesis CLI/transport/safety layer

Context: Telekinesis is an OS-agnostic accessibility MCP server (see README.md and
docs/DEMO-PLAN.md). The Linux backend (perception, actions, events) is done and owned
by Claude. **You own the entire `src/Telekinesis.Cli/**` layer** — transport, subcommands,
and new MCP tools. Do not edit `src/Telekinesis.Linux/**` or `src/Telekinesis.Abstractions/**`
except to add members to `IAccessibilityBackend` (coordinate first — Claude owns that file).

Build check: `dotnet build` at repo root must stay green. The MCP SDK is
`ModelContextProtocol` 2.2.0 (stable). Tools are `[McpServerToolType]` static classes
with `[McpServerTool]` methods; see `PerceptionTools.cs`/`ActionTools.cs` for the pattern.
`BackendProvider` is injected into tool methods and returns the connected backend.

## Task A — `assert` tool (unblocks demo 4)
Add an assertion tool (new file `AssertTools.cs`) so semantic UI tests can be scripted:
- `assert_element(role?, nameContains?, applicationId?, mustBe: "visible"|"enabled"|"exists", timeoutMs)`:
  poll `FindElementsAsync` until a match satisfies the condition or timeout; return
  `{ok, matched, waitedMs}`. Use `backend.WaitForEventAsync`/short polling.
- Exit-code contract for CI: a `telekinesis assert ...` CLI subcommand that returns 0/1
  so it drops into shell test scripts. (MauiDevFlow precedent: assert exits nonzero on failure.)

## Task B — remote transport + safety gate (unblocks demo 3)
- Add an SSE/HTTP transport option (`telekinesis serve --sse --port N`) alongside the
  default stdio, using the MCP SDK's HTTP transport. Bind localhost by default.
- **Session-enable gate:** actions are refused unless the server was started with
  `--enable-actions` (or a per-session unlock). `--read-only` already exists and must keep
  winning (perception only). Default posture when exposed over SSE = read-only.
- **File audit log:** every action tool call appends to `$XDG_STATE_HOME/telekinesis/audit.log`
  (fallback `~/.local/state/...`), in addition to the existing stderr line. Include timestamp,
  tool, target, success, path.
- Document the intended deployment: MCP behind the Lun.Os authenticated tunnel, never an
  open TCP port. Add a `docs/REMOTE.md`.

## Task C — credential-request handoff tool (unblocks demo 5)
- Add a `fill_credential(elementId, applicationId, field: "password"|"username"|...)` tool
  that DOES NOT accept or type a raw secret. It should trigger the host credential flow
  (password-manager handoff) so the value is entered without passing through the model or
  the server. If no credential provider is wired, return a clear "not available" result —
  never fall back to typing a secret from context.
- Ensure `read_element` on a `Protected` field keeps returning masked text (it does today);
  add a test/among the demo scenario that shows the mask.

## Task D — scenario runner format (coordinate with Claude)
Claude ships example scenarios in `demos/*.json`. Agree on this shape and build
`telekinesis run <scenario.json>` to execute it with caption output for filming:
```jsonc
{
  "name": "fill-out-contact",
  "narration": "Agent fills a contact form by name, not pixels",
  "steps": [
    { "say": "Locating the Name field",
      "tool": "find_elements", "args": { "role": "Edit", "nameContains": "Name" },
      "bind": "nameField" },                         // bind result[0] for later steps
    { "say": "Typing the name",
      "tool": "set_text",
      "args": { "elementId": "{{nameField.Ref.Id}}",
                "applicationId": "{{nameField.Ref.ApplicationId}}",
                "text": "Ada Lovelace" } }
  ]
}
```
Runner prints each `say` line (for caption overlay), executes the tool against the
backend, supports `{{bind.path}}` substitution from prior results, and stops on first
failure with a nonzero exit.

## Validated ground truth (from live Lun.Os container testing — build the runner to fit this)
The Linux backend is proven end-to-end against the real Lun.Os XFCE desktop. Design the
runner around these facts (see docs/RUNNING-ON-LINUX.md):
- **Native actions are the primary path and they work with no `/dev/uinput`.** `invoke`
  and `set_text` use AT-SPI `DoAction`/`EditableText` and return `path=NativeAction`.
  The runner should prefer these; treat injection (`click`-by-coord, `type_text`,
  `press_keys`) as the fallback that requires uinput passthrough.
- **Target already-registered apps.** In the webtop/XFCE container, freshly launched GTK3
  apps (e.g. mousepad) may not register (missing atk-bridge in the image); the running
  session apps (thunar `:1.5`, xfce4-panel `:1.4`, xfce4-terminal `:1.15`) do. `demos/thunar-navigate.json`
  is a VALIDATED flow — use it as the runner's first end-to-end test.
- **Multiple windows exist** per app id; `find_elements` may return several matches. The
  runner's `bind` should keep the first match and let steps disambiguate by name.
- **Verify by read-back**, not by assuming success: the thunar scenario confirms the
  navigation by re-reading the window title and asserting it changed. Support an
  `assert`/`expect` step field (see the scenario) so demos self-verify on camera.
- Element ids look like `:1.5|/org/gtk/WidgetFactory4/a11y/<uuid>` — treat opaque.

## Acceptance
- `dotnet build` green; `telekinesis doctor` unchanged.
- `telekinesis run demos/thunar-navigate.json` drives Thunar and self-verifies.
- Demos 3, 4, 5 have their enabling tool/flag and a `demos/*.json` scenario each.
- No secret ever reaches the model or the audit log.
