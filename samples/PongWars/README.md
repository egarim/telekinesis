# Pong Wars — an Avalonia app Telekinesis drives by control name

A [pong-wars](https://github.com/vnglst/pong-wars)-style day/night battle rendered on a
canvas, wrapped in a control panel of real, accessible controls. It exists to demo (and
stress-test) Telekinesis against a **stock Avalonia app**: on Windows, Avalonia exposes
full UI Automation out of the box — no special build target, no bridge (contrast with
[Uno on Windows](https://github.com/egarim/uno-atspi-bridge/blob/main/results/windows-uia-native.md),
which needs the non-default WinAppSDK head, and Uno/Skia on Linux, which needs the AT-SPI bridge).

The board itself is deliberately a11y-opaque (it's one custom-drawn control) — the point
is that everything an agent should touch is a *named element*:

| control | UIA role | driven with | pattern |
|---|---|---|---|
| Pause game / Reset board | Button | `--click "Pause game"` | Invoke |
| Day/Night speed | Slider | `--find "Day speed" --set-value 300` | RangeValue |
| Show grid lines | CheckBox | `--click "Show grid lines"` | Toggle |
| Balls per side | ComboBox | (probe/read) | Selection |
| Scores, status | Text | `--find "Paused"` | (read-back) |

Run it:

```
dotnet run --project samples/PongWars
```

Then, from the repo root (Windows):

```
telekinesis probe                                          # find the PongWars pid
telekinesis probe --app pid:<id> --depth 5                 # the whole panel, live scores included
telekinesis probe --enable-actions --app pid:<id> --click "Pause game"
telekinesis probe --enable-actions --app pid:<id> --find "Day speed" --set-value 300
telekinesis probe --enable-actions --app pid:<id> --click "Show grid lines"
```

Every action above runs `path=NativeAction` (UIA patterns, no input injection), and every
state change is verified by reading it back through UIA — the paused status text, the
slider value, the checkbox state, the live scores.

Video of a full driven session: [pongwars-uia-demo.mp4](pongwars-uia-demo.mp4)
