# NPU vs CPU, same model, same machine: I tried to benchmark it, and the result surprised me

The previous two posts set this up: I have a local AI "brain" for desktop automation, its
latency is almost all **prefill** (reading the prompt), and my Snapdragon Surface has a
45-TOPS NPU that nothing was using. The plan for this post was clean: run the *same model*
on the *same machine* on the NPU and on the CPU, and show you the speedup.

I got a result. It just wasn't the one I planned. This is what running LLMs on a
Windows-on-ARM NPU actually looks like in late 2025 — the good, and the wall.

## The setup was perfect on paper

Foundry Local ships Phi-3.5-mini in three variants that differ *only* in where they run —
same weights, same 131k context, different ONNX Runtime execution provider:

| Variant                              | Device | Execution provider      | Size   |
|--------------------------------------|--------|-------------------------|--------|
| `phi-3.5-mini-instruct-qnn-npu`      | NPU    | QNNExecutionProvider    | 2.0 GB |
| `Phi-3.5-mini-instruct-generic-cpu`  | CPU    | CPUExecutionProvider    | 2.5 GB |
| `Phi-3.5-mini-instruct-generic-gpu`  | GPU    | WebGpuExecutionProvider | 2.2 GB |

That is as clean as a hardware comparison gets on one box. The workload: one real turn of
my automation pilot — a 17-control Calculator screen, goal "compute 7 plus 7," asking for a
single JSON action, sent to each variant through Foundry's OpenAI-compatible endpoint.

## The CPU baseline (this worked)

`Phi-3.5-mini-instruct-generic-cpu`, same 311-token prompt, measured twice on the Snapdragon
X Elite:

| | Prompt tokens | Prefill (+1 tok) | Full 40-tok reply | Prefill speed |
|--|--:|--:|--:|--:|
| CPU / ONNX Runtime | 311 | ~9.7 s | ~18 s | ~33 tok/s |

Two things stand out. Prefill is ~33 tok/s — consistent with the ~40 tok/s I saw from
qwen3:4b on this CPU in [part 2](2-token-optimization.md), and the reason a real prompt
takes ~10 seconds. And decode is *slow*: the 40-token reply cost ~8.5s beyond prefill, only
~4.6 tok/s — noticeably worse than llama.cpp's ~32 tok/s on the identical CPU. Same silicon,
different runtime: **ONNX Runtime's generic CPU kernels decode far slower than llama.cpp's.**
Worth knowing before you blame the hardware.

## The NPU (this is the wall)

I loaded `phi-3.5-mini-instruct-qnn-npu` — Foundry confirmed it onto the NPU
(`QNNExecutionProvider`) — and sent the same prompt. It failed:

```
Non-zero status code while running GroupQueryAttention node
'/model/layers.0/attn/GroupQueryAttention':
seqlens_k[0] = 63 is out of range [0, 10)
```

Not a timeout, not slow — a hard runtime error in the very first attention layer. I shrank
the prompt to the two words "hi" (63 tokens once the chat template wraps it). Same error,
and the valid range had *shrunk* to `[0, 7)`. The NPU build fails on **every** prompt,
including a trivial one. It's a broken key/value-cache binding in the QNN export's
GroupQueryAttention node, in Foundry Local 0.10.3.

And here's why I didn't just try another model: **every modern small model — Phi-3/3.5,
the DeepSeek-R1 distills, Qwen — uses Grouped-Query Attention.** They'd almost certainly hit
the same node. This isn't one bad file; it's the GQA-on-QNN path in this shipping build.

## The honest result

I set out to publish "NPU is N× faster than CPU." What I can actually report is more useful,
because it's true:

- **The NPU is real and reachable.** Foundry Local installs, sees the Hexagon, loads QNN
  models onto it, and confirms the provider. The plumbing works.
- **The shipping model builds don't run.** The one NPU LLM I could pull for this box errors
  at inference on any prompt, and the failure is in a layer type every small model shares.
- **So the same-model NPU-vs-CPU benchmark can't be produced on this box today** — not
  because of the hardware or my code, but because the NPU model build is broken in the
  current release.
- **The CPU path works but ONNX Runtime is the wrong horse for decode** — llama.cpp is ~7×
  faster at generating tokens on the identical CPU.

## What I'd actually do today

For a local automation brain on a Snapdragon *right now*, the winning stack is not the NPU —
it's **llama.cpp/Ollama on the CPU, with a lean prompt** ([part 2](2-token-optimization.md)
cut my prefill ~3× for free). The NPU is the right long-term home for prefill-heavy
workloads — that's exactly what a 45-TOPS matrix engine is for — but the software is a
release or two away from delivering it. I'll re-run this exact benchmark the moment a
working GQA-on-QNN build ships; the test rig is sitting here ready.

The lesson I'm keeping: **measure the box you have, publish what it actually did.** A rigged
"NPU wins" number would have been easy and wrong. The real state of NPU LLM inference on
Windows-on-ARM in late 2025 is "the hardware is ready, the runtime isn't yet" — and that's
worth more to the next person than a hero chart.

*Part 3 of 3. Previous: [token optimization](2-token-optimization.md),
[NPU models on Surface](1-npu-models-on-surface.md).*
