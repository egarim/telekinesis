# Continuation prompt — build the Telekinesis Windows (UI Automation) backend

Paste everything below the line into a fresh Claude Code session **on a Windows machine
with the .NET 10 SDK installed**. It is self-contained.

---

You are continuing work on **Telekinesis**, an OS-agnostic accessibility MCP server that
lets AI agents see and control the desktop through platform accessibility APIs. The Linux
backend (AT-SPI) is complete and validated end-to-end. Your job is to implement the
**Windows backend using UI Automation (UIA)**, which is the richest of the three platform
APIs and the one the whole abstraction was modeled on — so it should map cleanly.

## Start here
1. Clone and build: `git clone https://github.com/egarim/telekinesis.git` then
   `dotnet build`. Read `README.md`, `docs/RUNNING-ON-LINUX.md` (for how the Linux backend
   was validated — the same patterns and gotchas apply), and the whole
   `src/Telekinesis.Abstractions/` project. **Do not modify Abstractions or
   Telekinesis.Linux.** You own a new `src/Telekinesis.Windows/` project only, plus the one
   Windows branch in `src/Telekinesis.Cli/BackendProvider.cs`.

## The contract to implement
Implement `IAccessibilityBackend` (in `Telekinesis.Abstractions/IAccessibilityBackend.cs`)
in a new class `UiaBackend : IAccessibilityBackend`. The interface is the single seam;
the MCP server and the `probe` CLI already call it and will work unchanged once your
backend is wired in. Members: `ConnectAsync`, `DiagnoseAsync`, `ListApplicationsAsync`,
`GetTreeAsync`, `FindElementsAsync`, `ReadElementAsync`, `GetFocusedAsync`,
`WaitForEventAsync`, `InvokeAsync`, `SetTextAsync`, `SetValueAsync`, `ClickAsync`,
`TypeTextAsync`, `PressKeysAsync`, `Name`, `DisposeAsync`.

The normalized model you must populate: `AccessibleElement` (Ref, Role, NativeRole, Name,
States, Bounds, Text, Value, Actions, ChildCount, Children), `AccessibleRole` (enum,
already modeled on UIA ControlTypes — mapping is nearly 1:1), `ElementState` (flags),
`ElementRef(Id, ApplicationId)`, `Bounds`, `ApplicationInfo`, `ElementQuery`,
`ActionResult` (with `ActionPath.NativeAction` vs `ActionPath.InputInjection`),
`AccessibilityEvent`, `DiagnosticReport`/`DiagnosticItem`, and `StaleElementException`.

## Recommended tech
- `net10.0-windows` TFP with `<UseWindowsForms>`/`<FrameworkReference Include="Microsoft.WindowsDesktop.App"/>`
  and use the managed **`System.Windows.Automation`** UIA client (UIAutomationClient +
  UIAutomationTypes) — fastest path and it exposes patterns/properties directly. If you
  prefer raw COM, CsWin32 over `IUIAutomation` also works. **FlaUI** (NuGet) is an
  acceptable convenience wrapper if you'd rather not touch UIA COM directly.
- Keep everything behind `OperatingSystem.IsWindows()`; the project only builds/runs on Windows.

## Mapping guide (UIA → normalized)
- **Applications** = children of `AutomationElement.RootElement`, grouped by
  `ProcessId`/top-level window. `ApplicationInfo.Id` = a stable per-process key; put the
  ProcessId in `ProcessId`.
- **Roles**: `ControlType` → `AccessibleRole` (Button→Button, Edit→Edit, Text→Text/Label,
  Window→Window, MenuItem→MenuItem, …). Always keep the UIA `LocalizedControlType` string
  in `NativeRole`.
- **States**: `IsEnabled`→Enabled, `!IsOffscreen`→Visible/Offscreen, `HasKeyboardFocus`→Focused,
  `IsKeyboardFocusable`→Focusable, `IsPassword`→**Protected** (never expose Text for these),
  Toggle/ExpandCollapse/SelectionItem patterns → Checked/Expanded/Collapsed/Selected,
  `ValuePattern.IsReadOnly`→ReadOnly, editable if a ValuePattern/TextPattern is present.
- **Bounds**: `BoundingRectangle` → `Bounds` (screen pixels). **Guard implausible values**
  (huge/negative — UIA uses `(0,0,0,0)` or offscreen sentinels) exactly like the Linux
  backend does; return null rather than let an agent click a bad target.
- **Text**: `ValuePattern.Current.Value` or `TextPattern` — never for Protected elements.
- **Actions (two tiers, native first)**: try the UIA pattern —
  `InvokePattern.Invoke()` for `invoke`, `ValuePattern.SetValue()` for `set_text`/`set_value`,
  `TogglePattern`/`ExpandCollapsePattern` as relevant — and report `path=NativeAction`.
  Fall back to **input injection via `SendInput`** (P/Invoke) at the element's bounds center
  for `click`, and keyboard events for `type_text`/`press_keys`, reporting
  `path=InputInjection`. (Windows needs no special permission for SendInput, unlike Linux
  uinput — but UIA across elevation boundaries needs the process elevated.)
- **Events**: `Automation.AddAutomationFocusChangedEventHandler` → feed `GetFocusedAsync`
  and `WaitForEventAsync("focus-changed", …)`. Mirror the Linux backend's waiter/TCS design.
- **Stable handles**: UIA `RuntimeId` churns; issue your own opaque `ElementRef.Id` and
  re-resolve on each action, throwing `StaleElementException` when the element is gone —
  same discipline as Linux.

## Wire-in and diagnostics
- In `src/Telekinesis.Cli/BackendProvider.cs`, replace the `OperatingSystem.IsWindows()`
  branch that throws `PlatformNotSupportedException` with `return new UiaBackend();` and add
  the project reference.
- `DiagnoseAsync` should confirm UIA is reachable and report readiness (mirror the Linux
  `doctor` shape). No bus/uinput checks needed on Windows.

## Validate exactly like Linux did
The `telekinesis probe` subcommand exercises the backend from the terminal — it is
cross-platform and needs no MCP client:
```
telekinesis doctor
telekinesis probe                       # list applications (open Notepad/Calculator first)
telekinesis probe --app <id> --depth 3  # walk a tree
telekinesis probe --find "Save"         # semantic search
telekinesis probe --enable-actions --click "Save"          # native invoke
telekinesis probe --enable-actions --app <id> --set-text "hello"  # native set_text + read-back
```
Prove, against a real app (Notepad, Calculator, File Explorer): list → tree → find →
native invoke → native set_text with read-back → focus event. Fix bugs you find; the Linux
backend surfaced real ones (stale-handle churn, junk bounds, connection edge cases) only by
running against real apps — expect the same.

## Deliverables
- `src/Telekinesis.Windows/` implementing `IAccessibilityBackend` via UIA, wired into
  `BackendProvider`, `dotnet build` green.
- Native-action-first with SendInput fallback; `path` reported correctly.
- Password fields marked `Protected`, text never exposed.
- A short `docs/RUNNING-ON-WINDOWS.md` with what you validated and any gotchas (mirror
  `docs/RUNNING-ON-LINUX.md`).
- Commit with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` and push to
  `github.com/egarim/telekinesis`. Coordinate: another session owns
  `src/Telekinesis.Cli/` transport/tools per `docs/CODEX-TASKS.md`; you only touch the
  one BackendProvider branch there.

Work autonomously, validate against real apps before claiming done, and report what you
actually verified.
