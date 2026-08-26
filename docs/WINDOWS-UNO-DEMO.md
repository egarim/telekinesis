# Surface (Windows) session brief — validate the UIA backend + Uno automation demo

Paste everything below the line into a fresh Claude Code session **on the Windows Surface
laptop** (needs the .NET 10 SDK). It does two things in one pass: validates Telekinesis's
Windows UI Automation backend on real hardware, then lands the Uno-app automation demo —
**with no accessibility bridge and none of the Linux SkiaSharp/FreeType pain**.

## Why Windows is the right machine for this
Uno's **native Windows head (WinAppSDK / WinUI 3)** renders *real* native controls, which
expose full **UI Automation** accessibility. This is the opposite of Uno's Skia heads on
Linux/macOS, whose canvas exposes no control-level a11y (that gap is exactly why the
`uno-atspi-bridge` had to exist on Linux). On Windows you need **no bridge**: Telekinesis's
UIA backend sees and drives the Uno controls directly.

---

You are validating **Telekinesis** (an OS-agnostic accessibility MCP server;
https://github.com/egarim/telekinesis) on Windows, then using it to drive a real Uno app.

### Part A — validate the Windows UIA backend (GitHub issue #1)
1. `git clone https://github.com/egarim/telekinesis.git`, then `dotnet build`. The Windows
   backend lives in `src/Telekinesis.Windows/` and is wired into `BackendProvider`; the CLI
   multi-targets `net10.0;net10.0-windows`.
2. Run against stock apps (open Notepad + Calculator first). On Windows the CLI runs its
   Windows head — use `dotnet run --framework net10.0-windows --project src/Telekinesis.Cli -- <args>`:
   ```
   telekinesis doctor
   telekinesis probe                       # list apps — expect Notepad, Calculator, etc.
   telekinesis probe --app <id> --depth 3  # walk a tree (real UIA controls)
   telekinesis probe --find "Save"
   telekinesis probe --enable-actions --click "Five"          # native InvokePattern on Calculator
   telekinesis probe --enable-actions --app <id> --set-text "hello"   # ValuePattern + read-back
   ```
   Fix any bugs the real apps surface (the Linux/macOS backends each turned up real bugs only
   under real apps — expect the same). Native-action-first (`InvokePattern`/`ValuePattern`),
   `SendInput` as the fallback; report `path` correctly; password fields marked `Protected`.

### Part B — the Uno automation demo (no bridge)
1. `git clone https://github.com/egarim/uno-atspi-bridge.git`. The sample app is
   `UnoApp/UnoDemo` — it already has clean controls with `AutomationProperties.Name`
   (Open File Manager, Save Document, Search box, Enable notifications, Volume slider,
   Theme selector). Its `AtspiBridge` is Linux-only; **you will not use it on Windows.**
2. Add the **native Windows head** to the app so controls are UIA-accessible. Edit
   `UnoApp/UnoDemo/UnoDemo.csproj`:
   - change `<TargetFrameworks>net10.0-desktop</TargetFrameworks>` to
     `<TargetFrameworks>net10.0-desktop;net10.0-windows10.0.19041.0</TargetFrameworks>`
     (this is the WinAppSDK head; adjust the SDK build number to one installed on the box).
   - Exclude the AT-SPI bridge from the Windows build so it still compiles: wrap the body of
     `UnoApp/UnoDemo/Atspi/AtspiBridge.cs` in `#if HAS_UNO_SKIA` / or simpler, guard the
     `AtspiBridge.TryStart(this);` call in `MainPage.xaml.cs` with
     `#if !WINDOWS` … `#endif` (the bridge is only meaningful on the Skia Linux head).
   - If the Uno.Sdk version (`global.json` = 6.6.42) doesn't offer the Windows head cleanly,
     the fallback is to create a fresh Uno app via `dotnet new unoapp` (default heads include
     Windows) and paste `MainPage.xaml` + the controls in.
3. Build & run the **Windows** head:
   `dotnet build UnoApp/UnoDemo/UnoDemo.csproj -f net10.0-windows10.0.19041.0` then run it so
   a native window opens.
4. Drive it with Telekinesis (Windows head): `telekinesis probe` should now list `UnoDemo`
   with its real controls (unlike the Skia heads, they appear). Then:
   ```
   telekinesis probe --app <UnoDemo id> --depth 6           # see Button/Edit/Slider/CheckBox/ComboBox
   telekinesis probe --enable-actions --click "Save Document"      # native invoke on an Uno button
   telekinesis probe --enable-actions --app <id> --set-text "invoices"   # into the Search box
   ```
   The win: an AI agent driving a **real Uno app** by control name, natively, no bridge.

### Deliverables
- Windows backend validated (Part A); commit any fixes and note results on issue #1.
- Uno demo working (Part B): a short capture of Telekinesis driving UnoDemo's controls on
  Windows. Note in the repo that the Uno path on Windows needs no bridge (native UIA), while
  Linux needs the bridge + a SkiaSharp/FreeType fix.
- Commit with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`, push to the repos.

Work autonomously, validate against real apps before claiming done, report what you verified.
