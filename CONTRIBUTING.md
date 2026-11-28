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

**Do not merge a PR while its `cla` check is red.** As of 2026-11-25 this is enforced by review
rather than by GitHub: `main` had no branch protection and the repository had no rulesets, so
`cla` was not a *required* status check and a merge was not technically blocked — PR #70 merged
with a red `cla` at merge time (run 45561034216 failed 26 minutes before the merge). Looking at
that PR today shows a *green* `cla`, which is the later `closed`-event run 45564282509 locking the
already-merged PR; it is not evidence the check passed before merging. Re-check the settings under
*Settings → Branches* and *Settings → Rules → Rulesets* rather than trusting this sentence — it
describes server-side state that lives outside the repository.

### For maintainers: the signature storage branch

Signatures are stored on the orphan branch
[`cla-signatures`](https://github.com/egarim/telekinesis/tree/cla-signatures) in
`.github/cla-signatures.json` — deliberately not on `main`, so signature history never
mixes with code history.

`contributor-assistant/github-action` reads *and writes* that file but **never creates the
branch**. While it was missing, every run that inspects signatures — a PR opened, a commit pushed
to one, or a `recheck` comment — logged this and then failed the check. Excerpt from
[run 45561034216](https://github.com/egarim/telekinesis/actions/runs/45561034216):

> Error occurred when creating the signed contributors file: Branch cla-signatures not found.
> Make sure the branch where signatures are stored is NOT protected.

followed by `Committers of pull request N have to sign the CLA`.

Being on `allowlist` did not help. Every commit on the then-open PRs #63, #64 and #66 resolves to
a linked `egarim` account — verified with
`gh api repos/egarim/telekinesis/pulls/<n>/commits`, which reported `"author": {"login":
"egarim"}` for all of them (`34e8975`, `ef0899b`, `b9f0888`; `68b1e1e`, `3bb5a1e`; `7069ed6`,
`63d03cd`, `5ad2506`, `89c87ee`, `3456583`, `580ed84`) — and all three still carried `cla` =
FAILURE: #63 and #64 from 2026-09-29, #66 from 2026-09-23.

Runs for a `pull_request_target: closed` event stayed green throughout, because they only lock
the already-merged PR and never touch the file. That is what made this outage look intermittent
rather than total.

Why allowlist membership did not help is settled by the pinned action's own source:
`contributor-assistant/github-action@v2.6.1` (SHA `ca4a40a7`, pinned at
`.github/workflows/cla.yml:21`) runs `getCommitters()` → `checkAllowList()` → `getFileContent()`
in `setupClaCheck()`. The allowlist does filter the signer list first. But on a 404 it calls
`createClaFileAndPRComment()`, which reports the storage error and then throws
`Committers of pull request N have to sign the CLA` **unconditionally** — so the storage failure
fails the run independently of who the committers were, and that second message does not imply
the committer list was unfiltered. Re-derive this from the pinned SHA if the action is ever
upgraded; it is an implementation detail, not a documented interface.

To restore it, create an **orphan** branch named `cla-signatures` containing one file at
`.github/cla-signatures.json`, holding a JSON object with an empty `signedContributors` array —
**unless signatures already existed**, in which case restore the previous array. Recreating the
branch empty discards every recorded signature and puts each past signer back in the red until
they sign again. That array has no other home: the branch is the only store, so the copy has to
come from somewhere that fetched it before the deletion —
`git show origin/cla-signatures:.github/cla-signatures.json` in any clone that still has the ref,
or a saved copy of the file. Leave the branch **unprotected**: the action pushes signature
commits to it with `GITHUB_TOKEN`, under this workflow's `contents: write` permission.

Verify **both** halves, because they fail in different ways and the read path alone can pass on a
repository that is still broken:

1. *Read path.* Open a PR, or push a commit to one, and read the `cla` job log: it must no longer
   contain `Error occurred when creating the signed contributors file`. Go by the log, not by the
   colour of the check — a red `cla` has a second, unrelated cause in this repository's history: a
   commit whose author email is not linked to any GitHub account, which the bot reports as
   *"… seems not to be a GitHub user"* and which restoring the branch cannot fix. Three commits on
   the PR #70 branch (`5f3d697`, `a3785cb`, `ca14eed`) were authored that way, by a machine whose
   global git identity was still the `Your Name` placeholder.
2. *Write path.* A **protected** branch that already contains the file passes step 1 and still
   rejects the first real signature, so confirm it is unprotected:
   `gh api repos/egarim/telekinesis/branches/cla-signatures/protection` must return **404
   "Branch not protected"**. Stronger evidence is a signature commit actually landing on the
   branch from a non-allowlisted signer; as of 2026-11-25 none has been observed since the branch
   was restored, so that path is **unverified**.

`recheck` is usable as a *detector* — its job log shows whether signature storage could be read —
but not as a *verdict* on an old PR. It refreshes the PR's status by asking GitHub to re-run the
*original* run, which GitHub refuses past 30 days (`Unable to retry this workflow run because it
was created over a month ago`). The recheck job itself succeeds and logs `All contributors have
signed the CLA`, yet the stale red check on the PR survives it; only a new commit on that PR
branch clears it.

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
