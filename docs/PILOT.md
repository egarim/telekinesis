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

Full flag set:

| Flag | Default | Effect |
|---|---|---|
| `--app pid:N` | — | **required**; the application to drive |
| `--max-steps N` | **12** | give up after N planned actions — the runaway-loop bound |
| `--model <name>` | `TELEKINESIS_BRAIN_MODEL`, else `qwen3:4b-instruct` | brain model |
| `--brain-url <url>` | `TELEKINESIS_BRAIN_URL`, else `http://localhost:11434` | any Ollama-compatible endpoint, including another machine on your LAN |
| `--dry-run` | off | plan without executing; the only way to run without `--enable-actions` |
| `--enable-actions` | — | required unless `--dry-run` |

The goal is the first non-flag argument. Missing goal or missing `--app` exits `2`
with usage. Exit `0` when the loop reports success, `1` otherwise — and the trace
file path is printed either way.

`pilot-eval` takes `--model` and `--brain-url` too, and reports steps, agreed,
invalid, agreement rate and latency (median and p95).

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

## System 1 — the Jev brain

`--brain jev` swaps the step policy for [TypeSafe's Jev](https://docs.typesafe.ai),
a "System 1" model: instead of *writing* JSON one token at a time, it answers
`choice` questions over option sets we define and returns the selection with a
probability distribution and a confidence. The action schema stops being
something we parse and becomes something the request enforces.

```
export TELEKINESIS_JEV_KEY=...            # required; no key, no start
telekinesis pilot "compute 7 plus 7" --app pid:N --brain jev --enable-actions
telekinesis pilot-eval <trace.jsonl> --brain jev
```

| | `TELEKINESIS_JEV_URL` | default `https://api.typesafe.ai/v1/systemone` |
|---|---|---|
| | `TELEKINESIS_JEV_MODEL` | default `jev-latest` |

The mapping is direct: `PilotAction` is one verb plus one candidate id, so the
request carries a `choice` over the verbs and a `choice` over the ids
`UiCandidates` already ranked — each id described by its role and label, which
is the same information the prose prompt carries. A `none` sentinel covers the
verbs that act on no element, and never escapes as a target.

The option set is trimmed to what can actually be completed from the current
state: with no candidates on screen there is no target question, so `click` is
not offered either. The two questions are answered independently in one pass, so
a `click` can still arrive paired with `none` — in that case the brain takes the
most probable real option, falling back to the head of the ranked candidate
list. That is a substitution, not a reading of intent, and it is made because an
action the loop rejects costs a retry and teaches a System-1 brain nothing.

### What it cannot do

**`type` is not offered at all.** It needs a generated string, and a System-1
model selects rather than writes. Offering a verb it can never complete would
only produce actions that `Validate` rejects, so the option set is
`click | press | scroll | wait | done`. `press` survives because a key
combination is enumerable.

Two consequences, stated plainly:

- Any task that needs text entry cannot be driven end-to-end by this brain today.
  The two-tier design — Jev decides every step, the LLM is woken *only* to fill
  `text` — is the obvious follow-up and is not implemented.
- **Replay agreement on a trace containing `type` steps is a floor, not a
  ceiling**: those steps can never agree. Compare on traces without them, or read
  the number knowing which way it is biased.

There is also no instruction channel — the system prompt is ignored, and the task
lives in each question's `instructions`. And unlike Ollama this is a **hosted**
API, which is a real departure from this tier's local-by-default posture: the
`state` payload is a screen dump, and screen dumps contain whatever the app
happened to be showing.

### What is worth measuring, and what is not

The 4B's problem was never schema validity — structured outputs already gave
100 % valid JSON and zero rejections, so "0 % structured-output errors" buys
nothing here. The claims worth testing are **latency** (70–500 ms against
0.8–1.7 s warm and 16–24 s cold) and whether grounding survives the loss of
free-form reasoning.

## Benchmark protocol — Whack-a-Mole

[`samples/WhackAMole`](../samples/WhackAMole/README.md) is the honest test for a
step policy, because the app is the referee: it timestamps spawn → click itself
and prints the latency, so the number is measured by the thing being clicked.

The published baseline — **46/46 hits, 110 ms average** — is a dumb 10 Hz retry
loop with *no model in the path at all*. That is the floor, not the target.
Inserting any brain can only make it slower, so the question the benchmark
answers is **how much a decision costs**, and whether the decision still lands
inside the target's lifetime.

The protocol is the lifetime slider: start at 3000 ms and walk it down until the
brain starts missing. The lowest lifetime a brain can still clear is its
reaction budget, refereed by the app.

| Brain | Expected decision | Fits a 3000 ms target? |
|---|---|---|
| dumb retry loop (no model) | — | 110 ms average, 0 misses |
| `qwen3:4b-instruct`, CPU | 800–1700 ms warm, 16–24 s cold | warm yes, cold never |
| `jev-latest` | 70–500 ms claimed | to be measured |

This isolates exactly what is in dispute. Whack-a-Mole is one repeated bounded
decision — "is the target up, and which candidate is it" — so the 4B's actual
weakness, multi-step planning, is not exercised at all. What remains is
grounding and latency, which is the comparison worth having.

**Not yet run.** The sample is Avalonia and the published figures are Windows
(UIA) on the Surface; these rows get filled in from a real round, not from
vendor numbers.

## Notes

- The pilot obeys the same safety posture as everything else: `--enable-actions`
  required to execute (or `--dry-run` to plan only); every action lands in the
  audit log.
- Latency expectations are hardware-bound: a quantized 4B on CPU answers a
  ~300-token candidate prompt in seconds; on a GPU box or Apple Silicon over
  the LAN it drops well under a second. Measure with pilot-eval before judging
  a model.
