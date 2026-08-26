# The vision tier — screenshot, parse_screen, click_at

Accessibility is always the first channel. But there are moments when nothing else
works: apps that never register on the a11y bus, custom-drawn/canvas UIs (games,
terminals, remote-desktop windows), trees that exist but lie. For those, Telekinesis
has a last-resort vision tier:

1. **`screenshot`** — capture the virtual desktop or a region as PNG (perception,
   available in `--read-only`).
2. **`parse_screen`** — screenshot + parse into UI elements via a
   [Microsoft OmniParser](https://github.com/microsoft/OmniParser) sidecar. Returns
   `[{type, content, interactive, bounds}]` with bounds in **screen pixels**.
3. **`click_at`** — pointer click at raw screen coordinates (action mode only), for
   targets that have pixel bounds but no element id.

The escalation flow an agent should follow: `find_elements`/`get_tree` →
(tree empty or wrong) → `screenshot` to look → `parse_screen` to get targets →
`click_at` to act → re-check with a11y or another screenshot.

Backends opt into the tier via `IScreenCaptureBackend` / `IPointerInjectionBackend`
in `Telekinesis.Abstractions`. Windows implements both (GDI capture + SendInput);
Linux and macOS report "not supported yet" until their capture lands.

## Running the OmniParser sidecar

OmniParser is a Python model service and stays out-of-process — Telekinesis only
speaks HTTP to it and degrades gracefully when it's absent (`doctor` shows the
`vision` check; `parse_screen` returns a setup hint).

```bash
git clone https://github.com/microsoft/OmniParser.git
cd OmniParser
conda create -n omni python==3.12 && conda activate omni
pip install -r requirements.txt
# download the V2 weights as described in the OmniParser README, then:
cd omnitool/omniparserserver
python -m omniparserserver   # serves on :8000
```

Telekinesis targets the official `omniparserserver` FastAPI contract:
`POST /parse/` with `{"base64_image": ...}` → `{"parsed_content_list": [...]}`, and
`GET /probe/` as the health check. Point Telekinesis elsewhere with:

```
TELEKINESIS_OMNIPARSER_URL=http://your-host:8000
```

A GPU host is strongly recommended — CPU-only parses of a full 4K desktop can take
tens of seconds (the client allows 120 s). Prefer region parses (`--region
"x,y,w,h"`) over full-desktop parses: faster, cheaper, and less for the model to
mislabel.

## Coordinate spaces — read this before trusting bounds

- **Screenshot pixels, OmniParser bounds, and `click_at` all share one coordinate
  space**: the physical virtual-desktop space that `SendInput` uses. A point you
  see in a capture is the point that gets clicked. This is validated end-to-end
  (click a pixel-verified button → its state visibly changes).
- **UIA element bounds are NOT guaranteed to match that space on multi-monitor,
  mixed-DPI setups.** Validated finding: on a 3-monitor system with different
  scale factors, a11y-reported bounds on secondary monitors were offset from the
  true pixels (see `docs/RUNNING-ON-WINDOWS.md`). On the primary monitor and on
  single-monitor systems they agree.
- Practical rule for agents: **when you escalate to vision, stay in vision.**
  Verify a target by looking at the pixels (`screenshot` of the target region)
  before `click_at`, and verify the effect the same way. Don't mix a11y bounds
  and pixel clicks across monitors.
- Z-order matters: accessibility trees report elements that are *behind* other
  windows with plausible bounds and `Visible` state. A pixel check before clicking
  is the only reliable "is it actually on top?" test — this is also why
  `parse_screen` (which sees only what's really on screen) can out-perform the
  a11y tree for targeting.

## Validation status

- `screenshot` (full + region) — validated live on Windows 11 (3-monitor, mixed DPI).
- `click_at` — validated live: pixel-verified coordinates, effect confirmed via the
  a11y tree afterward.
- `parse_screen` — validated against a mock implementing the official server
  contract (PNG accepted, ratio and absolute bboxes converted, region origin
  offset applied, degenerate boxes dropped). End-to-end run against a real
  OmniParser instance still pending — needs a GPU box or patience.
- Multi-monitor mixed-DPI alignment of *UIA bounds* is a known open issue; vision
  tier itself is self-consistent. Single-monitor validation recommended for
  anything precision-critical.
