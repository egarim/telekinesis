# Running Telekinesis on Windows — operational notes

Findings from live validation on Windows 11 Pro (arm64, .NET 10). UIA is the API the
normalized model was built on, so the mapping is close to 1:1 — the gotchas are mostly
about processes, DPI, and elevation, not about the tree.

## 1. Build/run the windows target
The CLI multi-targets. On Windows, run the `net10.0-windows` build so the UIA backend
is compiled in (the plain `net10.0` build reports "does not include the Windows backend"):

```bash
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- doctor
```

No setup is required otherwise — no bus, no udev, no permission grant. `doctor` checks
the UIA desktop root, elevation, and input capability.

## 2. Application ids are process ids
`ApplicationInfo.Id` is `pid:<ProcessId>`; "applications" are the top-level children of
the UIA desktop root grouped by process. Two consequences seen live:

- One process can own several windows — Windows 11 **Notepad is one process for all its
  windows and tabs**, so `pid:` trees include every open document. `get_tree` returns a
  synthetic `Application` root over all of the process's top-level windows.
- Element ids are backend-issued (`e1`, `e2`, …) and only valid for the session; the
  backend re-validates on every use and throws `StaleElementException` when the element
  (or its process) is gone — verified by killing the target app mid-session.

## 3. DPI awareness is load-bearing
The backend sets per-monitor-v2 DPI awareness on connect. Without it, a console host is
DPI-virtualized: UIA `BoundingRectangle` comes back in scaled coordinates while
`SendInput` wants physical ones, so injected clicks land off-target on any display over
100 % scaling. If you host the backend in your own process, don't override the DPI
context after connect.

## 4. Elevation (UIPI) is the Windows permission boundary
Unelevated Telekinesis cannot read the UIA trees of elevated apps, and `SendInput`
toward an elevated window is silently discarded by UIPI — the injector detects the
0-events-injected result and fails loudly instead. Run Telekinesis elevated to automate
elevated apps. `doctor` reports the current state as the `elevation` check.

## 5. Where the native paths work (validated)
- `invoke` → `InvokePattern.Invoke()` — clicked Calculator's **Seven** and verified the
  display read "Display is 7" afterward (`path=NativeAction`).
- `set_text` → `ValuePattern.SetValue()` — set a WinForms `TextBox` and verified by
  read-back through UIA (`path=NativeAction`). Classic Edit controls support this;
  Win11 Notepad's editor is a `Document` (TextPattern, no settable ValuePattern), so
  `set_text` there takes the injection fallback (click, ctrl+a, type).
- `set_value` → `RangeValuePattern.SetValue()` for sliders/spinners.
- Injection fallbacks — `click` (verified via a button whose label changes on click),
  `type_text` (KEYEVENTF_UNICODE, layout-independent, verified by read-back), and
  `press_keys` (`ctrl+a` chord verified) — all `path=InputInjection`.
- Focus: `get_focused` reads `AutomationElement.FocusedElement` directly;
  `wait_for_event("focus-changed", …)` fires from
  `AddAutomationFocusChangedEventHandler` — verified by clicking between two controls.
  Arm the waiter *before* the action; an action that doesn't move focus (clicking the
  already-focused control) produces no event.
- Password fields: `IsPassword` → role `PasswordEdit` + `Protected` state, text never
  read (verified: `Text` stays null on a `UseSystemPasswordChar` box with content).

## 6. Perception caps
The control-view walk caps children at 256 per node and searches at 20 000 nodes, so
browsers and virtualized grids can't hang a call. UIA additionally only materializes
virtualized list items that have been realized on screen — expect partial trees for
huge lists; scroll them (injection) to realize more.

## Validation quickstart
```
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- doctor
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- probe                      # open Notepad/Calculator first
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- probe --app pid:<id> --depth 3
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- probe --find "Seven"
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- probe --enable-actions --app pid:<calc> --click "Seven"
dotnet run --project src/Telekinesis.Cli -f net10.0-windows -- probe --enable-actions --app pid:<id> --find "<edit>" --set-text "hello"
```

## What's proven working
Validated live against Calculator, Windows 11 Notepad, and a WinForms test app:
- `doctor`, `list_applications`, tree walk (roles/names/bounds/text), `find_elements`
- Native `invoke` (Calculator button, effect verified on the display)
- Native `set_text` with UIA read-back verification
- Injected `click` / `type_text` / `press_keys` with effect verification
- `get_focused` + `wait_for_event("focus-changed")`
- `PasswordEdit`/`Protected` masking; `StaleElementException` on dead handles
