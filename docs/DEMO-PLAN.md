# Telekinesis — Demo Reel Plan (demos 1–5)

Goal: five short, high-impact videos that land one clear "wow" each in the first
10 seconds. All five run on a **Linux desktop session** (the Lun.Os VM is the target;
they cannot run on macOS). Each demo below has a shot list, the narration beat, the
tool sequence it exercises, and the build work it depends on.

Recording target: 30–60s per clip, terminal/chat on the left, the live desktop on the
right. Narration comes from the scenario runner's `say` lines (overlaid as captions).

## Production pipeline — record on the VM, polish with flick
The finished reel is produced with the **flick** skill (`/flick`), which turns a
transcript into original short-form scene animations (Remotion). Pipeline per demo:
1. Run the scenario on the VM and screen-record the real Telekinesis session (proof it's real).
2. The scenario's `say` lines double as the **flick transcript** — one narration beat per scene.
3. Feed the recording (or the transcript) to `/flick`: it plans one animation per scene,
   builds them with action-matched sound effects (typing, click, reveal), and renders the reel.
Keep the raw screen capture in the cut as the "receipts" moment; flick wraps it with the
hook, captions, and motion. Author `say` lines as spoken narration, not tool logs, so they
transcribe cleanly.

---

## 1. "Fill this out for me" — agent operates a real GUI app
**Wow:** a real desktop app gets driven, semantically, no cursor-teleport jank.
**Shot list:** type a plain request → app's fields fill, a dropdown opens, Save clicks.
**Narration:** "No screenshots — it reads the accessibility tree, like a screen reader does."
**Tool sequence:** `find_elements(role=Edit)` → `set_text` ×N → `invoke`(Save).
**Depends on:** nothing new. Core perception + actions (done). **First video to shoot.**
**App choice:** GNOME Contacts (new contact) or a small GTK form we ship in `demos/apps/`.

## 2. "Cross-app copy" — read from A, act in B
**Wow:** orchestration across two apps, not a scripted macro.
**Shot list:** read a total from a spreadsheet/webpage → switch window → enter it in an invoice field.
**Narration:** "Found total $1,240 → typing it into the invoice."
**Tool sequence:** `find_elements`(source) → `read_element` → `press_keys(alt+tab)` or `invoke`(target window) → `set_text`(dest). `wait_for("focus-changed")` to confirm the switch landed.
**Depends on:** event subscription (done) for reliable window-switch verification.

## 3. "Control my Linux box from my phone" — remote telekinesis
**Wow:** the signature clip. Commands from a remote device drive a remote GUI, no VNC.
**Shot list:** split screen — phone/laptop chat, remote Linux desktop reacting.
**Narration:** "The 'seeing' half is pure D-Bus — no screen-share, tiny bandwidth."
**Tool sequence:** any of the above, over a tunneled MCP transport.
**Depends on:** remote transport + session-enable gate + audit log (**Codex**).

## 4. "It survives a redesign" — semantic UI test automation
**Wow:** a coordinate-based tool breaks when the button moves; ours doesn't.
**Shot list:** `assert` finds & clicks Submit by name → change its CSS/position → re-run → still green, side-by-side with a pixel tool that fails.
**Narration:** "Same test, restyled button. Semantic targeting doesn't care."
**Tool sequence:** `find_elements(role=Button, nameContains=Submit)` → `assert`(visible+enabled) → `invoke`.
**Depends on:** an `assert` tool/subcommand (**Codex**).

## 5. "The password stays secret" — safety as a feature
**Wow:** defuses the "isn't this terrifying?" objection and turns it into a selling point.
**Shot list:** agent reads a full login form → prints every field EXCEPT the password (masked `[protected]`) → fills the password only via the password-manager handoff, never seeing it.
**Narration:** "It can read the form. It cannot read your password."
**Tool sequence:** `read_element`(form) shows `Protected` masking (done) → credential-handoff tool for the secret (**Codex**).
**Depends on:** `Protected` masking (done) + credential-request handoff tool (**Codex**).

---

## Work split

| Piece | Owner | Unblocks |
|---|---|---|
| Linux perception + actions + events | **Claude** — done | 1, 2, 4, 5 (read side) |
| Scenario runner + example scenarios | **Claude** | 1, 2 filming |
| `assert` tool | **Codex** | 4 |
| Remote transport (SSE / ssh-stdio) + `--enable` gate + file audit log | **Codex** | 3 |
| Credential-request handoff tool | **Codex** | 5 |
| Demo GTK sample app (fallback if no stock app fits) | **Claude** | 1 |

Ownership seam: Claude owns `src/Telekinesis.Linux/**` and `demos/**`; Codex owns
`src/Telekinesis.Cli/**` (transport, gate, new tools). The `IAccessibilityBackend`
contract and the MCP tool classes are the interface between them — neither edits the
other's tree, so the two streams merge cleanly.

## Definition of demo-ready
- `telekinesis doctor` green on the VM (a11y bus + uinput).
- Scenarios 1, 2, 4 run end-to-end via the runner with captions.
- Remote transport reachable over the Lun.Os tunnel with the enable-gate on.
- Credential handoff wired for scenario 5.
