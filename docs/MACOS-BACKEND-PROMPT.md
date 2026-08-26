# Continuation prompt — build the Telekinesis macOS (AXAPI) backend

Paste everything below the line into a fresh Claude Code session **on a macOS machine with
the .NET 10 SDK and Xcode command-line tools installed**. It is self-contained.

---

You are continuing work on **Telekinesis**, an OS-agnostic accessibility MCP server that
lets AI agents see and control the desktop through platform accessibility APIs. The Linux
backend (AT-SPI) is complete and validated end-to-end; a Windows (UIA) backend is tracked
separately. Your job is to implement the **macOS backend using the Accessibility API
(AXAPI / `AXUIElement`)**. This is the most hand-rolled of the three — no managed wrapper
exists, so you will P/Invoke into the `ApplicationServices` framework and marshal
CoreFoundation types.

## Start here
1. Clone and build: `git clone https://github.com/egarim/telekinesis.git` then
   `dotnet build`. Read `README.md`, `docs/RUNNING-ON-LINUX.md` (the validation patterns and
   gotchas transfer), and the whole `src/Telekinesis.Abstractions/` project. **Do not modify
   Abstractions, Telekinesis.Linux, or Telekinesis.Windows.** You own a new
   `src/Telekinesis.MacOS/` project only, plus the one macOS branch in
   `src/Telekinesis.Cli/BackendProvider.cs`.

## The contract to implement
Implement `IAccessibilityBackend` (in `Telekinesis.Abstractions/IAccessibilityBackend.cs`)
as `AxBackend : IAccessibilityBackend`. The interface is the single seam; the MCP server and
the `probe` CLI already call it and will work unchanged once wired in. Members: `ConnectAsync`,
`DiagnoseAsync`, `ListApplicationsAsync`, `GetTreeAsync`, `FindElementsAsync`,
`ReadElementAsync`, `GetFocusedAsync`, `WaitForEventAsync`, `InvokeAsync`, `SetTextAsync`,
`SetValueAsync`, `ClickAsync`, `TypeTextAsync`, `PressKeysAsync`, `Name`, `DisposeAsync`.

Populate the normalized model: `AccessibleElement` (Ref, Role, NativeRole, Name, States,
Bounds, Text, Value, Actions, ChildCount, Children), `AccessibleRole`, `ElementState`,
`ElementRef(Id, ApplicationId)`, `Bounds`, `ApplicationInfo`, `ElementQuery`, `ActionResult`
(`ActionPath.NativeAction` vs `InputInjection`), `AccessibilityEvent`,
`DiagnosticReport`/`DiagnosticItem`, `StaleElementException`.

## Tech
- `net10.0` TFM, guard everything behind `OperatingSystem.IsMacOS()`.
- P/Invoke into `/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices`
  (re-exports the HIServices `AXUIElement` API) and `CoreFoundation` for CFString/CFArray/
  CFType lifetime. You'll wrap `AXUIElementRef`/`CFTypeRef` as `IntPtr` and manage
  CFRetain/CFRelease carefully (Create/Copy → you own it → CFRelease).
- Consider a thin static interop class (`Ax`/`CF`) with the needed entry points; keep the
  CoreFoundation marshalling (CFStringCreate, CFStringGetCString, CFArrayGetCount/ValueAtIndex,
  AXValueGetValue for CGPoint/CGSize) in one place.

## Mapping guide (AXAPI → normalized)
- **Root & applications**: `AXUIElementCreateSystemWide()` for focus/system queries;
  enumerate running apps via their PIDs (e.g. `CGWindowListCopyWindowInfo` or the process
  list) and `AXUIElementCreateApplication(pid)` per app. `ApplicationInfo.Id` = a stable
  per-app key; put the pid in `ProcessId`.
- **Children**: `AXUIElementCopyAttributeValue(el, kAXChildrenAttribute)` → CFArray of
  AXUIElementRef. Depth-limit exactly like the Linux backend (never dump unbounded trees).
- **Roles**: `kAXRoleAttribute` (kAXButtonRole, kAXTextFieldRole, kAXStaticTextRole,
  kAXWindowRole, kAXMenuItemRole, kAXCheckBoxRole, …) → `AccessibleRole`. Keep the raw AXRole
  (and `kAXSubroleAttribute` when useful) in `NativeRole`.
- **States**: `kAXEnabledAttribute`→Enabled, `kAXFocusedAttribute`→Focused, `kAXHiddenAttribute`
  →Offscreen, `kAXSelectedAttribute`→Selected, `kAXExpandedAttribute`→Expanded;
  `kAXSecureTextFieldRole` (or the password subrole) → **Protected** (never expose Text).
- **Bounds**: read `kAXPositionAttribute` (AXValue kAXValueCGPointType) and `kAXSizeAttribute`
  (kAXValueCGSizeType) via `AXValueGetValue` → `Bounds`. **Guard implausible values**
  (huge/negative — the Linux backend hit `int.MinValue` sentinels and garbage extents;
  return null rather than let an agent click a bad target).
- **Text / Value**: `kAXValueAttribute` (string for text fields). Never for Protected elements.
- **Actions (two tiers, native first)**: `AXUIElementCopyActionNames` then
  `AXUIElementPerformAction(el, kAXPressAction)` for `invoke`;
  `AXUIElementSetAttributeValue(el, kAXValueAttribute, cfString)` for `set_text`/`set_value`;
  report `path=NativeAction`. Fall back to **input injection via `CGEvent`**
  (`CGEventCreateMouseEvent` + `CGEventPost` at bounds center for `click`;
  `CGEventCreateKeyboardEvent` for `type_text`/`press_keys`), reporting `path=InputInjection`.
- **Events**: `AXObserverCreate(pid, callback)` + `AXObserverAddNotification(observer, el,
  kAXFocusedUIElementChangedNotification, …)`, and attach the observer's run-loop source to a
  `CFRunLoop`. Feed `GetFocusedAsync` and `WaitForEventAsync("focus-changed", …)`; mirror the
  Linux backend's waiter/TCS design. You'll likely run a dedicated CFRunLoop thread.
- **Stable handles**: AXUIElementRefs are opaque and can go stale; issue your own opaque
  `ElementRef.Id`, re-resolve on each action, and throw `StaleElementException` when the
  element is gone — same discipline as Linux.

## Permissions (this is the macOS gotcha — handle it first)
AXAPI calls silently fail unless the process has **Accessibility (TCC) permission**.
- `DiagnoseAsync` must call `AXIsProcessTrusted()` and, if false, return a not-ready item
  telling the user to grant access in **System Settings → Privacy & Security → Accessibility**
  for the terminal (or the Telekinesis binary). Optionally call
  `AXIsProcessTrustedWithOptions` with the prompt option to surface the system dialog.
- Warn (in `doctor` and a `docs/RUNNING-ON-MACOS.md`) that permission is **per-binary and
  resets when the binary's code signature changes** — so a freshly rebuilt unsigned binary
  loses trust. Recommend granting the terminal, or codesigning the published binary, during
  development. The same TCC grant covers both reading (AXAPI) and `CGEvent` injection.

## Wire-in
In `src/Telekinesis.Cli/BackendProvider.cs`, replace the `OperatingSystem.IsMacOS()` branch
that throws `PlatformNotSupportedException` with `return new AxBackend();` and add the project
reference. (That file is otherwise owned by another session per `docs/CODEX-TASKS.md` — touch
only this one branch.)

## Validate exactly like Linux did
`telekinesis probe` exercises the backend from the terminal, no MCP client needed:
```
telekinesis doctor
telekinesis probe                       # list applications (open TextEdit/Calculator/Finder first)
telekinesis probe --app <id> --depth 3  # walk a tree
telekinesis probe --find "Save"         # semantic search
telekinesis probe --enable-actions --click "Save"                 # native AXPress
telekinesis probe --enable-actions --app <id> --set-text "hello"  # native set + read-back
```
Prove, against a real app (TextEdit, Calculator, Finder): list → tree → find → native invoke
→ native set_text with read-back → focus event. Fix bugs you find; the Linux backend surfaced
real ones (stale handles, junk bounds, connection edge cases, an overflow crash) only by
running against real apps — expect the same on macOS, plus the TCC permission dance.

## Deliverables
- `src/Telekinesis.MacOS/` implementing `IAccessibilityBackend` via AXAPI, wired into
  `BackendProvider`, `dotnet build` green.
- Native-action-first with CGEvent fallback; `path` reported correctly.
- Password fields marked `Protected`, text never exposed.
- `docs/RUNNING-ON-MACOS.md` covering the TCC permission steps and what you validated
  (mirror `docs/RUNNING-ON-LINUX.md`).
- Commit with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` and push to
  `github.com/egarim/telekinesis`.

Work autonomously, grant/verify Accessibility permission before assuming a call failed,
validate against real apps before claiming done, and report what you actually verified.
