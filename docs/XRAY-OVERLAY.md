# The X-ray overlay — see what the AI sees

A transparent, click-through, always-on-top window that draws labeled boxes over
real apps on the real desktop. It shows what Telekinesis perceives and what it is
about to do — the demo feature and a trust feature in one.

Three faces:

1. **`highlight` MCP tool** (perception, works in `--read-only`) — the agent draws
   a labeled box over an element or region for a moment: "this is what I'm looking
   at." Draws only; it can never take focus or input.
2. **X-ray mode** — `telekinesis probe --overlay --app pid:N` overlays numbered,
   labeled boxes over every named element of an app, refreshed every second (drag
   the window; the boxes follow). Interactive runs stop on Enter; `--for 30` runs
   timed (also handy for filming). `--find substr` filters what gets boxed.
3. **Show-intent mode** — with `TELEKINESIS_SHOW_INTENT=1`, every injected action
   (click, click_at, the set_text fallback) flashes its target for ~half a second
   *before* the input lands: the agent telegraphs its moves.

## Guarantees (validated live)

- **Click-through**: `WS_EX_TRANSPARENT` — verified by clicking a button *through*
  the active overlay; the click landed and the button reacted.
- **Never activates**: `WS_EX_NOACTIVATE` + `ShowWithoutActivation`; focus stays
  where the user had it. No taskbar/alt-tab entry (`WS_EX_TOOLWINDOW`).
- **Invisible to itself**: the overlay draws boxes from a11y bounds but is not part
  of any target app's tree, and Telekinesis screenshots *do* include it
  (BitBlt + `CAPTUREBLT`), so parse_screen and humans see the same screen.
- **Pixel-true on mixed DPI**: the overlay thread is per-monitor-v2 DPI aware and
  re-asserts the physical virtual-desktop rectangle via `SetWindowPos` after
  handle creation — WinForms otherwise rescales the window (found the hard way:
  a 3840x2744 desktop came back as a 3840x2189 window).

## Backend contract

`IVisualFeedbackBackend` in Abstractions (additive, opt-in like the vision tier):
`HighlightAsync(regions, duration)` — duration zero keeps the set until replaced
or `ClearHighlightsAsync`. Windows implements it with a WinForms color-keyed
layered window on a dedicated STA thread; Linux/macOS report not-supported until
their renderers land.

## Filming script (the video this was built for)

Scene 1 — "This is what the AI sees":
```
start calc
telekinesis probe                              # get Calculator's pid:N
telekinesis probe --overlay --app pid:N        # boxes bloom over every button
```
Drag the Calculator window mid-shot — the boxes chase it.

Scene 2 — "It acts through the same channel":
```
set TELEKINESIS_SHOW_INTENT=1
telekinesis probe --enable-actions --app pid:N --click "Seven"
telekinesis probe --enable-actions --app pid:N --click "Plus"
telekinesis probe --enable-actions --app pid:N --click "Seven"
telekinesis probe --enable-actions --app pid:N --click "Equals"
```
Each button flashes green before it visibly depresses; the display ends at 14.

Scene 3 — "Your agent, showing its work": Claude Desktop connected over MCP,
asked to do a small task, calling `highlight` before each `invoke` — the overlay
narrates the run.

## Gotchas

- One overlay per backend instance; a new `HighlightAsync` replaces the previous
  set (no stacking).
- The boxes are drawn from accessibility bounds — on mixed-DPI secondary monitors
  those can drift (issue #7); the overlay faithfully draws what UIA reports.
- Minimized windows report parked bounds at (-32000,-32000); Telekinesis now
  treats those as "no bounds" (they are neither clickable nor worth boxing).
