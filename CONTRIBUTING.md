# Contributing to Telekinesis

Contributions are welcome — bug reports, backend work (the macOS AXAPI backend is
implemented but has never been exercised on real hardware; live validation is the big
open front), samples, and docs.

## The CLA (required)

Telekinesis is dual-licensed (AGPL-3.0 + commercial, see [COMMERCIAL.md](COMMERCIAL.md)).
That model only works if the project can relicense every contribution, so **all
contributors must sign the [Contributor License Agreement](CLA.md)** — it's a license,
not a copyright transfer; you keep ownership of your work.

Signing is automated: when you open your first pull request, the CLA bot asks you to
post one comment on the PR:

> I have read the CLA Document and I hereby sign the CLA

A red CLA check is expected to stop a PR from merging. Note that this is currently enforced
by review rather than by GitHub: `main` has no branch protection and the repository has no
rulesets, so `cla` is not a *required* status check and a merge is not technically blocked.

### For maintainers: the signature storage branch

Signatures are stored on the orphan branch
[`cla-signatures`](https://github.com/egarim/telekinesis/tree/cla-signatures) in
`.github/cla-signatures.json` — deliberately not on `main`, so signature history never
mixes with code history.

`contributor-assistant/github-action` reads that file but **never creates the branch**. If it
goes missing, every check that actually inspects signatures — a PR opened, a commit pushed to
one, or a `recheck` comment — fails while setting up storage:

> Error occurred when creating the signed contributors file: Branch cla-signatures not found.
> Make sure the branch where signatures are stored is NOT protected.

and the run then fails with `Committers of pull request N have to sign the CLA` regardless of
who the committers were. Runs for a `pull_request_target: closed` event stay green, because
they only lock the already-merged PR and never read the file — which is what made this outage
look intermittent rather than total.

To restore it, create an **orphan** branch named `cla-signatures` containing one file at
`.github/cla-signatures.json`, holding a JSON object with an empty `signedContributors` array.
Leave the branch **unprotected**: the action pushes signature commits to it with
`GITHUB_TOKEN`, under this workflow's `contents: write` permission.

Verify by opening a PR, or pushing a commit to an existing one, and confirming the `cla` check
goes green. Do **not** verify with `recheck` on an old PR: it refreshes the PR's status by
asking GitHub to re-run the *original* run, which GitHub refuses past 30 days
(`Unable to retry this workflow run because it was created over a month ago`). The recheck
itself still succeeds and logs `All contributors have signed the CLA`, but the stale red check
survives it — only a new commit on that PR branch clears it.

## Development

- .NET 10 SDK. `dotnet build` at the repo root builds everything; the CLI multi-targets
  `net10.0;net10.0-windows`. Tests: `dotnet test src/Telekinesis.Cli.Tests`.
- Validate against real apps before claiming a backend change works: `telekinesis doctor`,
  then drive the samples in [`samples/`](samples/) (`dotnet run --project samples/PongWars`
  and friends) via `telekinesis repl --enable-actions`.
- Perception before action: every action must try the native accessibility pattern first
  and report `path` truthfully; fallbacks are `InputInjection`, never silently.
- Password fields stay `Protected` — never expose their content through any code path.

### Where things live

| Project | Contains |
|---|---|
| `Telekinesis.Abstractions` | `IAccessibilityBackend`, the normalized role/state vocabulary, `TerminalScreen`, `IConsoleSession` — no platform code |
| `Telekinesis.Windows` | UIA backend, ConPTY, SendInput, GDI capture |
| `Telekinesis.Linux` | AT-SPI backend, uinput injection, Unix PTY |
| `Telekinesis.MacOS` | AXAPI backend, CGEvent injection |
| `Telekinesis.Cli` | MCP tools, the CLI, the provider registry — everything user-facing |
| `Telekinesis.Vision` | OmniParser client and perceptual memory |
| `Telekinesis.Pilot` | the local-model planning loop |
| `Telekinesis.Medium*` | the annotation packages and their generators |

### Adding an MCP tool

The perception/action split is a **security boundary**, not a naming convention.
Perception tools load in `--read-only` and over `serve` without `--enable-actions`;
action tools do not. Put a tool in the action tier if it can change anything the user
would care about — including reading something the user would not have exposed.
`browser_evaluate` is an action *because JavaScript in a logged-in page is the user's
whole account*, even though the caller only wants a value back.

1. Add the method to the right class — `PerceptionTools` / `ActionTools` (or a new
   `[McpServerToolType]` class registered next to them in `Program.cs`).
2. Registration happens twice, for stdio and for `serve` — update both, or the tool
   silently exists over one transport only.
3. Every action must `AuditLog.Append` — including failures.
4. Write the `[Description]` for a model that cannot read the source. State what the
   tool does *not* do, and any ordering requirement, because that is what an agent gets
   wrong. Then document it in the tool table in [README.md](README.md) and in the
   relevant `docs/` page.
5. If it takes a `max`/`limit`, clamp it and say the clamp in the description.

### Documentation

Treat a behaviour a user could be surprised by as undocumented until it is written
down: buffer capacities, truncation caps, timeouts, what is *not* replayed or captured,
and every default. [docs/CLI.md](docs/CLI.md) is the command/flag/environment reference
and should never drift from `Program.cs`.
