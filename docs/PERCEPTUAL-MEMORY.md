# Perceptual memory — learning from previous vision runs

Every `parse_screen` costs seconds of model time and used to throw its knowledge
away. Perceptual memory keeps it: screens already seen answer instantly, targets
already used become recallable anchors, and the whole store doubles as a
training-ready dataset for a future model adapter.

## What it does

**Parse cache.** Captures are fingerprinted with a 64-bit perceptual hash (dHash);
a screen within a small Hamming distance of a cached one returns its elements
immediately with `"source":"memory"` — measured ~3x faster than even a *mock*
parser round-trip, and orders of magnitude faster than a real OmniParser parse.
Pass `applicationId` to `parse_screen` to enable it (identity is the process
name, so memory survives restarts and pid churn).

**Element anchors.** When a `click_at` lands inside an element of the most recent
parse, that element proved itself useful and is remembered: caption, type, a pixel
crop, and its position normalized to the app's window rectangle (survives window
moves). `recall_targets` re-locates an app's anchors on the live screen by
normalized cross-correlation around the expected spot — no parser call at all —
and returns current pixel bounds ready for `click_at`.

**Feedback loop.** Each recall updates the anchor: hits refresh its position
(drift adapts), misses accumulate, and an anchor with 3+ misses exceeding its
hits is evicted with its crop. Memory self-corrects when apps change.

**Flatness guard.** Near-flat crops (blank background) would template-match
anywhere, so they are never recorded as anchors in the first place.

## Store and commands

`%LOCALAPPDATA%\Telekinesis\perceptual-memory` (override `TELEKINESIS_MEMORY_DIR`):
`parse-cache.jsonl`, `anchors.jsonl`, `crops/*.png`.

```
telekinesis memory                     # stats: cached parses, anchors, location
telekinesis memory export --out <dir>  # dataset.jsonl + crops/
```

## The dataset / model-adapter angle

`memory export` emits one JSONL line per anchor:

```json
{"image":"crops/42e5….png","caption":"Mock settings icon","type":"icon",
 "app":"ScratchTarget","bbox_normalized":[0.150,0.242,0.199,0.198],
 "hits":0,"misses":0,"last_seen":"2026-08-28T…"}
```

Grounded crops, captions, normalized boxes, and verified-use counts — collected
as a side effect of normal agent work, with zero labeling effort. This is
exactly the raw material for fine-tuning a small local grounding model or LoRA
adapter that knows *your* applications (a per-user OmniParser specialization).
Training itself is future work; this store guarantees the data exists.

## Validated (mock OmniParser + WinForms target, Windows 11)

- Cache: first parse `source=omniparser` (540 ms incl. capture), identical screen
  `source=memory` (172 ms, no parser call).
- Anchor learned on click; recalled on the unmoved window at NCC score 0.995.
- Eviction: 3 failed recalls (corrupted crop) removed the anchor and its file.
- Export: valid dataset.jsonl + crops.

## Known limitations

- **Self-similar UIs can alias.** After a window move, a crop of one of two
  visually identical controls (e.g. twin text boxes) can re-locate onto its twin
  with a high score. The flatness guard filters blank crops, but identical
  repeated controls remain ambiguous — prefer anchoring distinctive elements,
  and treat `recall_targets` scores as confidence, not proof. Verify effects
  after acting, as always.
- Anchors need `applicationId` at parse time; a parse without it caches under
  the generic `"screen"` key and learns no anchors.
- One search radius (±96 px around the expected spot): a window resized
  drastically or a control that moved further than that reads as a miss.
- Raster ops need a platform codec; Windows only for now (`GdiRasterCodec`).
  Linux/macOS report memory as unavailable until theirs land.
