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

## 6. Multi-monitor + mixed DPI: UIA bounds can lie off the primary monitor
Validated on a 3-monitor system with different scale factors (primary at 150 %):
screenshot pixels, OmniParser output, and `click_at`/`SendInput` all share one
coordinate space and stay consistent with each other — but **UIA element bounds on
secondary monitors came back offset from the true pixels**, even with per-monitor-v2
awareness set. Windows also relabels controls' physical size per monitor (a 140x23
logical button reports 210x35 at 150 %).

Consequences:
- On single-monitor systems and on the primary monitor, a11y bounds and pixels agree
  — clicks on a11y centers land correctly (all section-5 validation ran there).
- On secondary monitors, don't click a11y-reported centers blindly. Escalate to the
  vision tier: `screenshot` the region, confirm the target pixels, `click_at` what
  you see (see `docs/VISION.md`).
- Z-order is invisible to the a11y tree: an element reports `Visible` with plausible
  bounds while another window covers it. The pointer paths (`click`, and the injection
  fallback under `invoke`) now **hit-test the click point** with `WindowFromPoint` and
  refuse with "element is covered by another window" rather than clicking through to
  whatever is on top. Native actions (`invoke`/`set_value`/`set_text`) don't depend on
  being on top and are unaffected — prefer them.

**Self-check.** `telekinesis doctor` now reports a `dpi-awareness` line: `per-monitor`
means element bounds match physical pixels on every monitor; `system`/`unaware` means
bounds on scaled secondary monitors may drift, and you should use the self-contained
single-file build (it ships a per-monitor-v2 manifest) or escalate to the vision tier
there. The programmatic switch usually succeeds under the `dotnet` host, but this tells
you for certain on the machine in front of you.

## 7. Perception caps
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


## Single-file publish is not supported on Windows

`System.Windows.Automation` (the managed UIA client, part of WPF) fails its type
initializer inside a `PublishSingleFile=true` bundle — `doctor` reports the
`uia-root` check failing with "The type initializer for
'System.Windows.Automation.CacheRequest' threw an exception" (issue #26; reproduced
live on Windows ARM64, and present in the shipped v0.6.0/v0.7.0 single-file zips).

The build now fails fast if you try. Supported Windows paths, all verified live:

- `dotnet tool install -g Telekinesis` (framework-dependent — the recommended install)
- folder publish: `dotnet publish -f net10.0-windows -r win-x64 --self-contained`
  (what `scripts/release.sh` ships in the release zips since v0.8.0)
