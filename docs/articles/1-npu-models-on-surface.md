# Running local AI models on the NPU of a Snapdragon Surface — with Foundry Local

I have a Windows on ARM machine — a Surface with a **Snapdragon X Elite (X1E80100)**. Like
every Copilot+ PC it ships with a **Hexagon NPU rated at 45 TOPS**, and like most people I
had never run a single inference on it. The GPU and CPU do the work; the NPU sits there
looking impressive on the spec sheet.

This is the note I wish I'd had: how to actually put a language model on that NPU, why it
matters, and the one gotcha that wasted an afternoon.

## The context: a local "brain" for desktop automation

I've been building [Telekinesis](https://github.com/egarim/telekinesis), an MCP server
that lets an AI agent drive desktop apps through the accessibility tree instead of
screenshots. It has an experimental *pilot* mode: a small local model looks at a compact
list of on-screen controls and picks one action per step. The whole point is that it runs
**locally** — no cloud, no per-token bill, works offline.

My first attempt used Ollama. It worked, and it was **slow** — 12 to 23 seconds per
decision. That number is the reason this article exists, because the fix turned out to be
"use the hardware I already owned."

## Why the NPU, specifically

When you send a prompt to a language model, two very different things happen:

- **Prefill** — the model reads your prompt. This is one big matrix multiply over all the
  input tokens at once. It's compute-bound and *embarrassingly parallel*.
- **Decode** — the model writes the answer, one token at a time. This is memory-bandwidth
  bound and inherently sequential.

For an automation brain, the *output* is tiny (a 15-token JSON action) but the *input* is
substantial (the list of on-screen controls). So the cost is almost entirely prefill —
exactly the batched-matmul workload an NPU is built to eat. Ollama on the ARM CPU prefills
at roughly 40 tokens/second; that's where the 12+ seconds went. The NPU should demolish it.

The catch: **Ollama does not use the NPU.** Neither does most of the llama.cpp ecosystem
today. To reach the Hexagon you need a runtime that speaks **QNN** (the Qualcomm Neural
Network execution provider for ONNX Runtime). The easiest one on Windows is Microsoft's
**Foundry Local**.

## Installing Foundry Local

It's a winget package:

```
winget install Microsoft.FoundryLocal
```

On Windows on ARM the package is the native arm64 build. After install you get a `foundry`
CLI. First real command:

```
foundry model list
```

which starts a local server and prints the catalog. The column that matters is **Device**:

```
| Model Name        | Type | Size    | Device |
|-------------------|------|---------|--------|
| phi-3.5-mini      | Chat | 2.0 GB  | NPU    |
| phi-3-mini-4k     | Chat | 3.5 GB  | NPU    |
| deepseek-r1-7b    | Chat | 3.7 GB  | NPU    |
| deepseek-r1-14b   | Chat | 7.1 GB  | NPU    |
| phi-4             | Chat | 8.4 GB  | GPU    |
| mistral-7b-v0.2   | Chat | 4.1 GB  | GPU    |
| ...                                          |
```

`Device: NPU` means Foundry has a **QNN-optimized ONNX build** of that model. When the
server starts you'll see it confirm the provider:

```
QNNExecutionProvider: 0%
● success: Server ready (http://127.0.0.1:60202)
```

`QNNExecutionProvider` is the Hexagon NPU. That line is the whole point — it's the proof
your model will run on the neural engine and not fall back to CPU.

Pull an NPU model:

```
foundry model download phi-3.5-mini
foundry model run phi-3.5-mini
```

## Talking to it: it's just an OpenAI endpoint

Foundry Local exposes an **OpenAI-compatible** API on localhost. That is the best part —
you don't learn a new SDK. Anything that speaks `/v1/chat/completions` points at it with a
one-line base-URL change:

```csharp
var client = new OpenAIClient(
    new ApiKeyCredential("not-needed-locally"),
    new OpenAIClientOptions { Endpoint = new Uri("http://127.0.0.1:PORT/v1") });
```

For Telekinesis' pilot brain, wiring it up was exactly that: point the existing HTTP client
at Foundry's port instead of Ollama's `11434`. The model, the prompt, and the JSON-schema
output constraint were unchanged.

## The gotcha that cost an afternoon: session 0

I drive this Surface headless, over SSH, from a Mac. Two things bit me, both the same root
cause — **an SSH session on Windows runs in session 0, not the interactive desktop
session**:

1. `winget`'s app-execution-alias isn't on the session-0 PATH. You have to call the real
   binary under `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe`
   (and the same trick for `foundry.exe`).
2. Anything that reads *display* geometry from session 0 sees a fake 1024×768 headless
   adapter, not your real monitors.

Foundry Local itself is fine headless — it's a localhost HTTP service, no desktop needed —
but the tooling around it assumes you're logged in at the machine. Worth knowing before
you conclude "it's broken."

## Is it worth it?

Short version: **yes, for prefill-heavy local workloads on a Copilot+ PC.** The NPU is the
difference between a local model that's a curiosity and one that's usable. The model
catalog is small and Microsoft-curated (Phi, DeepSeek-R1 distills, a few others) rather
than the full Hugging Face firehose, but for the "small local brain" use case that's
exactly the right shortlist.

In the next two posts I measure it: first the prompt-side optimization that cut my latency
before I even touched the NPU, then a head-to-head of the *same workload* on NPU versus CPU
on this one machine — which turned into a more honest story than I expected about the state
of NPU LLM inference on Windows-on-ARM right now.

*Part 1 of 3. Next: [shrinking the prompt](2-token-optimization.md).*
