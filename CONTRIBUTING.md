# Contributing to Telekinesis

Contributions are welcome — bug reports, backend work (the macOS AXAPI backend is the
big open front), samples, and docs.

## The CLA (required)

Telekinesis is dual-licensed (AGPL-3.0 + commercial, see [COMMERCIAL.md](COMMERCIAL.md)).
That model only works if the project can relicense every contribution, so **all
contributors must sign the [Contributor License Agreement](CLA.md)** — it's a license,
not a copyright transfer; you keep ownership of your work.

Signing is automated: when you open your first pull request, the CLA bot asks you to
post one comment on the PR:

> I have read the CLA Document and I hereby sign the CLA

PRs cannot merge until the CLA check is green.

## Development

- .NET 10 SDK. `dotnet build` at the repo root builds everything; the CLI multi-targets
  `net10.0;net10.0-windows`.
- Validate against real apps before claiming a backend change works: `telekinesis doctor`,
  then drive the samples in [`samples/`](samples/) (`dotnet run --project samples/PongWars`
  and friends) via `telekinesis repl --enable-actions`.
- Perception before action: every action must try the native accessibility pattern first
  and report `path` truthfully; fallbacks are `InputInjection`, never silently.
- Password fields stay `Protected` — never expose their content through any code path.
