# The local UI brain — `telekinesis pilot`

A small local model as the low-latency step policy for accessibility-tree
control (issue #10). Most UI-driving steps aren't broad reasoning — they're
structured decisions over the current tree — so a 4B-class local model with a
hard-constrained action schema can carry the loop, keeping the big remote model
out of the per-step path entirely.

## Usage

```
ollama pull qwen3:4b-instruct           # or any model you prefer
telekinesis pilot "compute 7 plus 7" --app pid:N --enable-actions
telekinesis pilot "..." --app pid:N --dry-run       # plan without executing
telekinesis pilot-eval <trace.jsonl> --model qwen3:8b   # offline model comparison
```

Configuration: `TELEKINESIS_BRAIN_URL` (default `http://localhost:11434` — any
Ollama-compatible endpoint, including another machine on your LAN) and
`TELEKINESIS_BRAIN_MODEL` (default `qwen3:4b-instruct`); `--brain-url`/`--model`
override per run.

## The loop

1. **Inspect** — snapshot the app and preprocess it into a compact, *ranked*
   candidate list (≤20 entries: `{id, role, label, value}`, short ids `c1…`).
   Interactive, visible, enabled, named elements only; goal-keyword matches and
   editable/button roles rank first. The model never sees the raw tree.
2. **Decide** — the brain replies with exactly one schema-constrained action
   (`click | type | press | scroll | wait | done` + target/text). Output is
   enforced by Ollama structured outputs at temperature 0.
3. **Validate** — unknown actions, missing fields, or a target id that isn't in
   the current candidate list are rejected; the rejection reason is fed back for
   one corrective retry (and logged — rejections are training signal too).
4. **Execute** — native-first backend actions: click→`invoke`, type→`set_text`,
   press→`press_keys`. All the existing guards apply (occlusion, audit log).
5. **Observe** — the acted-on element is read back and the observation goes into
   the next prompt. `done` is only credible because the model can see effects.
6. **Stop** — on `done`, a step budget, three failed actions, or a stall (the
   same action three times).

## Traces — the training dataset

Every run appends one JSONL file under the state dir (`pilot-traces/`):
run header (goal, brain), one line per step (screen, ranked candidates, raw
model output, parsed action, validation error if any, executor result,
observation, brain/act latency), and the final outcome. That is exactly the
shape issue #10 calls for: prompted-baseline evals, LoRA fine-tuning of the
3B–4B pilot, distillation targets for a sub-1B action router, and recovery
training from failures — no extra labeling pass needed.

`telekinesis pilot-eval <trace.jsonl>` replays recorded steps through any brain
**without touching the UI** and reports action-agreement rate plus latency
median/p95 — the harness for walking the model-size ladder down (4B → 2B → …).

## Benchmark — qwen3:4b-instruct, Windows-on-ARM CPU, Calculator "7+7"

Measured with this harness (traces in the repo issue; reproduce with the
commands above):

| Metric | Result |
|---|---|
| Schema compliance (structured outputs) | 100 % valid JSON, 0 rejections |
| Decision latency, warm | ~0.8–1.7 s |
| Decision latency, cold prompt | 16–24 s (first step / cache miss) |
| Action execution (native invoke) | ~270 ms |
| Live success, scripted mock policy | 5/5 steps, display verified 14 |
| Live success, qwen3:4b prompted | 0/2 runs (looped 7+, never planned Equals) |
| Replay agreement vs the correct trace | 40 % (2/5 steps) |

Two findings drove fixes that are now part of the loop: without **readouts** the
brain is open-loop (it clicked 7+ five times, ending at 35, because it couldn't
see the display), and a naive same-action stall guard misses A-B-A-B cycles
(now detected).

**Decision (issue #10 acceptance):** a prompted 4B is *not* yet a reliable UI
pilot for multi-step plans on this hardware — it grounds well (always picked
real, sensible targets; zero schema violations) but misplans sequencing. The
infrastructure is the deliverable: schema + validation + traces + replay eval
make the next rungs cheap — LoRA fine-tuning on accumulated traces (every run
adds data), an 8B teacher for comparison via `--model`, and distillation into a
sub-1B action router once behavior stabilizes. A GPU/LAN endpoint via
`TELEKINESIS_BRAIN_URL` removes the latency wall independently.

## Notes

- The pilot obeys the same safety posture as everything else: `--enable-actions`
  required to execute (or `--dry-run` to plan only); every action lands in the
  audit log.
- Latency expectations are hardware-bound: a quantized 4B on CPU answers a
  ~300-token candidate prompt in seconds; on a GPU box or Apple Silicon over
  the LAN it drops well under a second. Measure with pilot-eval before judging
  a model.
