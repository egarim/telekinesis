# The cheapest 3x speedup: stop sending your local model verbose JSON

Before I moved my local automation brain onto the NPU (that's the
[next post](3-npu-vs-cpu.md)), I found a speedup that cost nothing and needed no new
hardware: **I was feeding the model twice the tokens it needed.**

This is a small, boring, high-leverage optimization, and it generalizes to any local model
you prompt in a loop.

## Where the time actually goes

My Telekinesis pilot loop asks a small model, once per step, "here is the goal and here are
the on-screen controls — pick one action." On a Snapdragon Surface running qwen3:4b on the
CPU, each step took ~12 seconds, and I assumed the model was just slow.

It wasn't. Ollama reports timing, and it's damning:

```
prompt: 545 tokens  ->  13,749 ms   (prefill — reading the prompt)
output:  16 tokens  ->     754 ms   (decode  — writing the answer)
```

The answer took **three-quarters of a second**. Reading the prompt took **fourteen
seconds**. On a CPU, prefill is the whole cost, and prefill scales with how many tokens you
send. So the question isn't "how do I get a faster model" — it's "why am I sending 545
tokens to ask for a 16-token answer?"

## The culprit: verbose JSON

My turn message encoded the candidate controls as a JSON array, because JSON is the
reflexive choice:

```json
{"goal":"compute 7 plus 7","screen":"Calculator","readouts":["Display is 0"],
 "candidates":[
   {"id":"c1","role":"button","label":"One","value":""},
   {"id":"c2","role":"button","label":"Two","value":""},
   {"id":"c7","role":"button","label":"Seven","value":""},
   ... 17 of these ...
]}
```

Look at what every element pays for: `{"id":"`, `","role":"`, `","label":"`, `","value":""}`.
That punctuation is **the same on every line**, it carries no information the model needs,
and the tokenizer charges you for all of it. Plus `"value":""` — an empty field, sent 17
times.

## The fix: a terse line format

Same information, written the way you'd write it for a human skimming a table:

```
goal: compute 7 plus 7
screen: Calculator
readouts:
  Display is 0
candidates (id role "label" [=value]):
  c1 button "One"
  c2 button "Two"
  c7 button "Seven"
  ...
```

Each candidate is now `c7 button "Seven"` — the id, the role, the label, and nothing else.
Empty values simply don't appear. The model still replies with the exact same JSON action
schema; only the **input** changed. One detail that makes this safe: describe the format in
the system prompt ("candidates — one per line as `<id> <role> "<label>" [=value]`") so the
model isn't guessing at the shape.

## The measurement

Same machine, same model (qwen3:4b on the Snapdragon CPU), same 17-candidate Calculator
turn, only the encoding changed:

| Encoding            | Prompt tokens | CPU prefill |
|---------------------|--------------:|------------:|
| Verbose JSON        |           459 |     12.0 s  |
| Terse lines         |           171 |      4.2 s  |
| **Change**          |     **−63 %** | **−65 %**   |

**2.7× fewer tokens, 2.9× faster** — for a change that is strictly a serialization choice.
No accuracy trade-off, because no information was removed; the model sees the same controls
with the same ids.

## Why this matters more locally than in the cloud

In a hosted model you pay for tokens in dollars, and prefill on a datacenter GPU is so fast
you never feel it. Locally, on a CPU or a modest NPU, **prefill is wall-clock latency you
feel on every single step of an agent loop.** Halving the tokens halves the time the human
waits. It compounds: a 10-step task just went from two minutes to forty seconds before I
changed a single thing about the model or the hardware.

The general rule I took away: **when you prompt a local model in a loop, treat the prompt
like bandwidth, not like a data structure.** JSON is for machines that parse; a model reads
text, and text wants to be terse. Reserve the structure for the *output*, where a schema
buys you a reliable parse — that's the half worth spending tokens on.

Next: now that the prompt is lean, I move the same workload onto the Snapdragon's NPU and
measure it against the CPU on the same machine.

*Part 2 of 3. Previous: [NPU models on Surface](1-npu-models-on-surface.md). Next:
[NPU vs CPU, same model, same machine](3-npu-vs-cpu.md).*
