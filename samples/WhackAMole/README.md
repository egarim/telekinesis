# Whack-a-Mole — how fast can an agent react to the UI?

A reaction benchmark where the **app is the referee**: a target button spawns at a random
position on a canvas, and the app timestamps spawn → click, in milliseconds, on screen.
Whatever drives it can't flatter itself — the latency shown is measured by the thing
being clicked.

Run the app, then drive it with Telekinesis's persistent session (`repl`) so you measure
the action loop, not process startup:

```
dotnet run --project samples/WhackAMole

telekinesis repl --enable-actions
> click pid:<id> Start round
> click pid:<id> Hit target        # loop this — a miss just prints (no match)
```

## Measured results (Surface Laptop 7, Windows 11 ARM64, warm repl)

A dumb 10 Hz retry loop (`click … Hit target` every 100 ms, no event subscription, no
prediction) over a ~45-second round:

| metric | value |
|---|---|
| Hits / misses | **46 / 0** (target lifetime 3000 ms) |
| Average reaction (spawn → invoke) | **110 ms** |
| Best | **27 ms** |
| Worst observed | ~200 ms |
| Per-command cost in `repl` (find + InvokePattern) | 34–49 ms |

Every hit is `path=NativeAction` — the UIA InvokePattern, no pointer movement, which is
why reaction time is independent of where the target spawns.

The same loop through one-shot `probe` calls would report ~3–5 **seconds** per attempt —
that's .NET process startup + UIA connect, not the action. `repl` (connect once, then
commands on stdin with per-command timing) exists exactly to make this distinction visible.

Video of a full round: [whackamole-uia-demo.mp4](whackamole-uia-demo.mp4)
